using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gestaobim.Ai;

/// <summary>Contas de assinatura que o gateway sabe usar por OAuth.</summary>
public sealed record OAuthProvider(string Id, string Name, string Flag, string ShortName);

/// <summary>
/// Login OAuth das assinaturas (Claude Code, ChatGPT Codex, Antigravity): roda o proprio gateway com a opcao de login,
/// que abre o navegador padrao e grava o token na pasta de contas (so do usuario). Nao passa por nenhuma tela nossa.
/// </summary>
public static class OAuthLogin
{
    public static readonly IReadOnlyList<OAuthProvider> Providers =
    [
        new("claude", "Claude Code (assinatura Claude)", "-claude-login", "Claude Code"),
        new("codex", "ChatGPT Codex (assinatura ChatGPT)", "-codex-login", "ChatGPT Codex"),
        new("antigravity", "Antigravity (Gemini)", "-antigravity-login", "Antigravity"),
    ];

    /// <summary>Contas ja conectadas: um arquivo .json por conta na pasta de contas.</summary>
    public static IReadOnlyList<string> ConnectedAccounts()
    {
        try
        {
            return Directory.Exists(GbAiPaths.AuthDir)
                ? Directory.GetFiles(GbAiPaths.AuthDir, "*.json").Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).OrderBy(n => n).ToList()
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    public static void Disconnect(string accountFileName)
    {
        var name = Path.GetFileName(accountFileName);   // nunca sai da pasta de contas
        var path = Path.Combine(GbAiPaths.AuthDir, name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Abre o login do provedor. Devolve a lista de contas depois, e se surgiu uma conta nova.</summary>
    public static async Task<(bool Added, IReadOnlyList<string> Accounts, string Output)> LoginAsync(OAuthProvider provider, TimeSpan timeout, CancellationToken cancel = default)
    {
        var exe = GbAiPaths.ProxyExe;
        if (!File.Exists(exe)) throw new GatewayException($"O gateway de IA nao foi encontrado: {exe}.");
        if (!string.Equals(GatewayHost.Sha256Of(exe), ProxyPin.ExeSha256, StringComparison.OrdinalIgnoreCase))
            throw new GatewayException("O gateway de IA nao e a versao aprovada (SHA-256 diferente).");
        GbAiPaths.EnsurePrivate(GbAiPaths.DataDir);
        GbAiPaths.EnsurePrivate(GbAiPaths.AuthDir);
        GbAiPaths.EnsurePrivate(GbAiPaths.RunDir);

        var before = ConnectedAccounts().ToHashSet();
        // O login nao sobe servidor, mas le o mesmo config: usa um proprio, na pasta protegida, apagado no fim.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var config = Path.Combine(GbAiPaths.RunDir, $"login-{Environment.ProcessId}-{provider.Id}.yaml");
        File.WriteAllText(config, GatewayConfig.Build(55999, token, token + "00", GbAiPaths.AuthDir), new UTF8Encoding(false));
        var output = new StringBuilder();
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = GbAiPaths.RunDir,
            };
            foreach (var arg in new[] { "-config", config, "-local-model", provider.Flag }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new GatewayException("Nao foi possivel iniciar o login.");
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                if (cancel.IsCancellationRequested) throw;
                throw new GatewayException("O login demorou demais e foi cancelado.");
            }
        }
        finally
        {
            try { File.Delete(config); } catch (Exception) { }
        }
        var after = ConnectedAccounts();
        string text;
        lock (output) text = Redactor.Scrub(output.ToString());
        return (after.Any(a => !before.Contains(a)), after, text);
    }
}
