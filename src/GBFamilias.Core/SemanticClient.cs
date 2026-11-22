using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Gestaobim.Familias.Core;

public sealed class SemanticException(string message) : Exception(message);

/// <summary>
/// Sobe o "gbfam serve" como processo filho e fala com ele por HTTP em 127.0.0.1, com um token aleatorio.
/// O servidor carrega os modelos de IA uma vez, entao cada busca leva centenas de milissegundos. Ele encerra
/// sozinho se o Revit morrer (stdin fechado). Nunca toca no Revit: so le o banco.
/// </summary>
public sealed class SemanticClient : IDisposable
{
    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly string _token;

    private SemanticClient(Process process, HttpClient http, string token)
    {
        _process = process;
        _http = http;
        _token = token;
    }

    public bool IsAlive => !_process.HasExited;

    /// <summary>Inicia o servidor e espera ele ficar pronto (os modelos levam alguns segundos para carregar).</summary>
    public static async Task<SemanticClient> StartAsync(string gbfamExe, string dbPath, TimeSpan timeout, CancellationToken cancel = default)
    {
        if (!File.Exists(gbfamExe)) throw new SemanticException($"gbfam.exe nao encontrado: {gbfamExe}");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var info = new ProcessStartInfo(gbfamExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,   // mantido aberto: se o Revit morrer, o servidor percebe e sai
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(gbfamExe)!,
        };
        foreach (var arg in new[] { "--db", dbPath, "serve", "--token", token, "--exit-on-stdin-eof" }) info.ArgumentList.Add(arg);

        var process = Process.Start(info) ?? throw new SemanticException("Nao foi possivel iniciar o gbfam.exe.");
        var port = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line && line.StartsWith("LISTENING ", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(10), out var p)) port.TrySetResult(p);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.Exited += (_, _) => port.TrySetException(new SemanticException("O gbfam.exe encerrou antes de ficar pronto."));
        process.EnableRaisingEvents = true;

        try
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(timeout);
            var listening = await port.Task.WaitAsync(timer.Token).ConfigureAwait(false);
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{listening}"), Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("X-GBFam-Token", token);
            return new SemanticClient(process, http, token);
        }
        catch (Exception error)
        {
            TryKill(process);
            string detail;
            lock (errors) detail = errors.ToString().Trim();
            throw new SemanticException(error is OperationCanceledException
                ? "O servidor de busca demorou demais para iniciar."
                : $"{error.Message} {detail}".Trim());
        }
    }

    /// <summary>Ids em ordem de relevancia. <paramref name="allowedIds"/> restringe o ranking aos que passam nos filtros.</summary>
    public async Task<IReadOnlyList<long>> SearchAsync(string? text, byte[]? image, IEnumerable<long>? allowedIds, int top, CancellationToken cancel = default)
    {
        var body = new JsonObject { ["top"] = top };
        if (!string.IsNullOrWhiteSpace(text)) body["q"] = text;
        if (image is not null) body["image_b64"] = Convert.ToBase64String(image);
        if (allowedIds is not null) body["ids"] = new JsonArray(allowedIds.Select(i => (JsonNode?)i).ToArray());

        using var response = await _http.PostAsJsonAsync("/search", body, cancel).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        var node = JsonNode.Parse(json) as JsonObject;
        if (!response.IsSuccessStatusCode)
            throw new SemanticException(node?["error"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}");
        return (node?["hits"] as JsonArray)?.Select(h => h?["id"]?.GetValue<long>() ?? -1).Where(i => i >= 0).ToList() ?? [];
    }

    public void Dispose()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            _http.PostAsync("/shutdown", null, cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
        _http.Dispose();
        if (!_process.WaitForExit(1500)) TryKill(_process);
        _process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }
}
