using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Gestaobim.Familias.Core.Server;

public enum DownloadStatus
{
    AlreadyPresent,
    Downloaded,
}

public sealed record DownloadResult(DownloadStatus Status, string Path);

/// <summary>A pasta onde as familias baixadas ficam: obrigatoria, de livre escolha (local ou de rede).</summary>
public static class DownloadFolder
{
    public const int MaxPathLength = 150;

    /// <summary>Mensagem em portugues se a pasta nao serve, ou null se serve. Cria a pasta se ela nao existir.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Escolha a pasta onde as famílias baixadas serão guardadas.";
        path = path.Trim();
        if (!Path.IsPathFullyQualified(path)) return "Use um caminho completo (por exemplo, C:\\Familias ou \\\\servidor\\pasta).";
        if (path.Length > MaxPathLength) return $"O caminho da pasta é longo demais ({path.Length} caracteres; o máximo é {MaxPathLength}). O Revit não abre caminhos muito compridos.";
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".gbfam-teste-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"Não consigo gravar nessa pasta: {error.Message}";
        }
    }

    public static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
}

/// <summary>
/// Garante que o .rfa de uma familia esta na pasta de downloads: baixa do servidor, um de cada vez, confere o
/// SHA-256 do catalogo local e so entao publica o arquivo. Seguro com pasta de rede compartilhada: cada maquina grava
/// num .part proprio e o arquivo final nunca e sobrescrito por outro.
/// </summary>
public sealed class DownloadManager(ServerApi api, string machineName)
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly string _machine = Sanitize(machineName);

    public const int MaxRateLimitWaits = 5;

    public int Waiting => _waiting;
    private int _waiting;

    private static string Sanitize(string s) => new string(s.Where(char.IsLetterOrDigit).Take(20).ToArray()) is { Length: > 0 } ok ? ok : "pc";

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>O arquivo local com o hash certo? Barato para a biblioteca (famílias têm poucos MB).</summary>
    public static bool IsValidLocal(string path, string sha) =>
        File.Exists(path) && string.Equals(Sha256OfFile(path), sha, StringComparison.OrdinalIgnoreCase);

    public async Task<DownloadResult> EnsureAsync(FamilyRecord record, string downloadsRoot, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        var final = FamiliasPaths.ResolveFamilyPath(downloadsRoot, record.RelPath)
                    ?? throw new InvalidOperationException("O caminho da familia no catalogo e invalido.");

        Interlocked.Increment(ref _waiting);
        try
        {
            await _one.WaitAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting); // tambem quando o usuario cancela enquanto esperava na fila
        }
        try
        {
            if (IsValidLocal(final, record.Sha)) return new DownloadResult(DownloadStatus.AlreadyPresent, final);
            if (File.Exists(final)) File.Delete(final); // corrompido ou de outra versao: baixa de novo

            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            var part = $"{final}.{_machine}.part";
            if (!IsValidLocal(part, record.Sha))
            {
                for (var integrityAttempt = 0; ; integrityAttempt++)
                {
                    await DownloadWithRetriesAsync(record, part, progress, cancel).ConfigureAwait(false);
                    if (string.Equals(Sha256OfFile(part), record.Sha, StringComparison.OrdinalIgnoreCase)) break;
                    File.Delete(part);
                    if (integrityAttempt >= 1) throw new IntegrityException("O arquivo baixado nao confere com o catalogo (SHA-256 diferente), mesmo depois de baixar de novo.");
                }
            }

            Publish(part, final, record.Sha);
            return new DownloadResult(DownloadStatus.Downloaded, final);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task DownloadWithRetriesAsync(FamilyRecord record, string part, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        for (var wait = 0; ; wait++)
        {
            try
            {
                var adapter = progress is null ? null : new Progress<(long done, long total)>(p => progress.Report(new DownloadProgress(DownloadPhase.Downloading, p.done, p.total, 0)));
                await api.DownloadFamilyAsync(record.Sha, part, adapter, cancel).ConfigureAwait(false);
                return;
            }
            catch (RateLimitedException limited) when (wait < MaxRateLimitWaits)
            {
                var seconds = Math.Clamp(limited.RetryAfterSeconds, 1, 60);
                for (var left = seconds; left > 0; left--)
                {
                    progress?.Report(new DownloadProgress(DownloadPhase.WaitingForSlot, 0, 0, left));
                    await Task.Delay(TimeSpan.FromSeconds(1), cancel).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Move o .part para o nome final sem sobrescrever o que outra maquina possa ter publicado antes.</summary>
    private static void Publish(string part, string final, string sha)
    {
        try
        {
            File.Move(part, final, overwrite: false);
        }
        catch (IOException) when (File.Exists(final))
        {
            // outra maquina publicou primeiro: se o arquivo dela e valido, usa o dela
            if (IsValidLocal(final, sha)) File.Delete(part);
            else File.Move(part, final, overwrite: true);
        }
    }
}

public enum DownloadPhase
{
    Downloading,
    WaitingForSlot,
}

public sealed record DownloadProgress(DownloadPhase Phase, long Done, long Total, int WaitSeconds);

/// <summary>
/// Mantem a pasta de downloads dentro do limite apagando as familias menos usadas. Nunca mexe em pasta de rede e, com
/// <paramref name="deletable"/>, so apaga os arquivos dessa lista (os que o plugin baixou): as familias que o usuario ja
/// tinha na pasta contam no total, mas nunca sao apagadas.
/// </summary>
public static class CacheEvictor
{
    public static long Evict(string root, long limitBytes, IReadOnlyCollection<string>? protect = null, TimeSpan? minAge = null,
        IReadOnlySet<string>? deletable = null)
    {
        if (limitBytes <= 0 || DownloadFolder.IsNetworkPath(root) || !Directory.Exists(root)) return 0;
        var guarded = new HashSet<string>((protect ?? []).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var recent = DateTime.UtcNow - (minAge ?? TimeSpan.FromMinutes(10));
        var full = Path.GetFullPath(root);
        var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var files = new DirectoryInfo(root).EnumerateFiles("*.rfa", options)
            .Select(f => (File: f, Used: Latest(f.LastAccessTimeUtc, f.LastWriteTimeUtc)))
            .ToList();
        var total = files.Sum(f => f.File.Length);
        var freed = 0L;
        foreach (var (file, used) in files.OrderBy(f => f.Used))
        {
            if (total - freed <= limitBytes) break;
            if (used > recent || guarded.Contains(file.FullName)) continue;
            if (deletable is not null && !deletable.Contains(file.FullName[prefix.Length..].Replace('/', '\\').Trim('\\'))) continue;
            try
            {
                var size = file.Length;
                file.Delete();
                freed += size;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return freed;
    }

    private static DateTime Latest(DateTime a, DateTime b) => a > b ? a : b;
}
