using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gestaobim.Familias.Core.Server;

/// <summary>
/// Quais familias ja estao na pasta das familias baixadas. Uma unica varredura da pasta (em vez de um acesso a disco
/// por familia), refeita depois de cada download e quando a janela volta ao foco. Reconhece a familia:
/// 1. no mesmo caminho relativo do catalogo, com o mesmo tamanho (o que o plugin baixou, ou a biblioteca copiada inteira);
/// 2. senao, pelo nome do arquivo e o tamanho, em qualquer subpasta (familias que o usuario ja tinha, organizadas do jeito dele).
/// O SHA-256 so e conferido ao inserir; se nao conferir, o plugin baixa a familia do servidor.
/// </summary>
public sealed class DownloadedIndex
{
    private Dictionary<string, long> _files = new(StringComparer.OrdinalIgnoreCase);
    // nome do arquivo + tamanho -> caminho relativo (o primeiro encontrado)
    private Dictionary<(string Name, long Length), string> _byName = new(NameComparer.Instance);
    private readonly object _lock = new();

    public string? Root { get; private set; }
    public int Count { get { lock (_lock) return _files.Count; } }

    private static string Key(string relativePath) => relativePath.Replace('/', '\\').Trim('\\');
    private static string FileName(string key) => key.LastIndexOf('\\') is >= 0 and var i ? key[(i + 1)..] : key;

    /// <summary>Varre a pasta. Pasta inexistente ou ilegivel = nada baixado (nunca lanca).</summary>
    public void Refresh(string? root)
    {
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<(string Name, long Length), string>(NameComparer.Instance);
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            try
            {
                var full = Path.GetFullPath(root);
                var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
                foreach (var file in new DirectoryInfo(full).EnumerateFiles("*.rfa", options))
                {
                    var key = Key(file.FullName[prefix.Length..]);
                    files[key] = file.Length;
                    byName.TryAdd((file.Name, file.Length), key);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // varredura incompleta: o que ja foi lido vale
            }
        }
        lock (_lock)
        {
            Root = root;
            _files = files;
            _byName = byName;
        }
    }

    /// <summary>Esta na pasta (no caminho do catalogo ou, pelo nome e tamanho, em outra subpasta)?</summary>
    public bool IsDownloaded(FamilyRecord record) => FindRelative(record) is not null;

    /// <summary>
    /// O caminho relativo (a partir da pasta) do arquivo que corresponde a familia, ou null. Prefere o caminho do
    /// catalogo; sem ele, um arquivo com o mesmo nome e tamanho em qualquer subpasta.
    /// </summary>
    public string? FindRelative(FamilyRecord record)
    {
        var key = Key(record.RelPath);
        lock (_lock)
        {
            if (_files.TryGetValue(key, out var length) && (record.SizeBytes <= 0 || length == record.SizeBytes)) return key;
            if (record.SizeBytes > 0 && _byName.TryGetValue((FileName(key), record.SizeBytes), out var other)) return other;
            return null;
        }
    }

    public void MarkDownloaded(FamilyRecord record, long length)
    {
        var key = Key(record.RelPath);
        lock (_lock)
        {
            _files[key] = length;
            _byName.TryAdd((FileName(key), length), key);
        }
    }

    /// <summary>Esquece um arquivo (por exemplo, o que tinha o nome certo mas nao conferiu o SHA-256).</summary>
    public void Forget(string relativePath)
    {
        var key = Key(relativePath);
        lock (_lock)
        {
            if (_files.Remove(key, out var length) && _byName.TryGetValue((FileName(key), length), out var mapped)
                && string.Equals(mapped, key, StringComparison.OrdinalIgnoreCase))
                _byName.Remove((FileName(key), length));
        }
    }

    private sealed class NameComparer : IEqualityComparer<(string Name, long Length)>
    {
        public static readonly NameComparer Instance = new();
        public bool Equals((string Name, long Length) a, (string Name, long Length) b) =>
            a.Length == b.Length && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Name, long Length) v) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(v.Name), v.Length);
    }
}

/// <summary>
/// Quais arquivos foram baixados PELO PLUGIN (os unicos que a limpeza do cache pode apagar): as familias que o usuario
/// ja tinha na pasta nunca sao apagadas. Um arquivo de texto por pasta de downloads, uma linha por caminho relativo.
/// </summary>
public sealed class DownloadLedger(string file)
{
    private readonly object _lock = new();

    public void Add(string relativePath)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllLines(file, [relativePath.Replace('/', '\\').Trim('\\')]);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // sem o registro, a limpeza simplesmente nao apaga este arquivo (o lado seguro)
            }
        }
    }

    public HashSet<string> Load()
    {
        lock (_lock)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(file))
                    foreach (var line in File.ReadAllLines(file))
                        if (!string.IsNullOrWhiteSpace(line)) set.Add(line.Trim());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
            return set;
        }
    }

    /// <summary>O arquivo do registro para uma pasta de downloads (fica no perfil do usuario, nunca dentro da pasta).</summary>
    public static string FileFor(string localHome, string downloadsRoot)
    {
        var full = Path.GetFullPath(downloadsRoot).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        return Path.Combine(localHome, "baixadas", hash + ".txt");
    }
}

/// <summary>Mensagens em portugues para os erros do servidor (a interface so mostra o texto).</summary>
public static class ServerMessages
{
    public static string Describe(Exception error) => error switch
    {
        AuthRequiredException e => e.Message,
        NotAuthorizedException e => e.Message,
        FamilyNotOnServerException => "Esta família não está no servidor. Peça ao administrador para publicar o catálogo de novo.",
        RateLimitedException e => $"O servidor limitou os downloads: {e.Message} Tente de novo em instantes.",
        IntegrityException e => e.Message,
        CatalogRejectedException e => e.Message,
        ServerUnavailableException e => $"Sem conexão com o servidor. {e.Message}\n\nAs famílias já baixadas continuam disponíveis.",
        OperationCanceledException => "Cancelado.",
        InvalidDataException e => $"O servidor enviou um arquivo inválido: {e.Message}",
        IOException e => $"Erro ao gravar na pasta de downloads: {e.Message}",
        UnauthorizedAccessException e => $"Sem permissão para gravar na pasta de downloads: {e.Message}",
        _ => error.Message,
    };

    /// <summary>Esse erro pede que o usuario entre de novo (e vale tentar depois do login)?</summary>
    public static bool NeedsLogin(Exception error) => error is AuthRequiredException;
}
