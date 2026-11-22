using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Gestaobim.Familias.Core.Server;

public sealed class RateLimitedException(int retryAfterSeconds, string reason) : Exception(reason)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class FamilyNotOnServerException(string message) : Exception(message);

public sealed class NotAuthorizedException(string message) : Exception(message);

public sealed class IntegrityException(string message) : Exception(message);

/// <summary>Cliente HTTP do servidor de arquivos: so conhece o SHA-256 da familia, nunca caminhos internos.</summary>
public sealed class ServerApi
{
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly Func<CancellationToken, bool, Task<string>> _token;

    /// <param name="token">Entrega o access token; o segundo argumento pede renovacao forcada (depois de um 401).</param>
    public ServerApi(HttpClient http, string apiBase, Func<CancellationToken, bool, Task<string>> token)
    {
        _http = http;
        _apiBase = apiBase.TrimEnd('/');
        _token = token;
    }

    /// <summary>Sempre enviamos um User-Agent identificando o plugin e a versao.</summary>
    public static void ConfigureClient(HttpClient http, string version)
    {
        http.DefaultRequestHeaders.UserAgent.Clear();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GBFamilias", version));
    }

    private async Task<HttpResponseMessage> GetAsync(string path, long? rangeStart, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _token(cancel, attempt > 0).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, _apiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (rangeStart is > 0) request.Headers.Range = new RangeHeaderValue(rangeStart, null);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new ServerUnavailableException($"Sem conexao com o servidor: {error.Message}");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                continue; // renova o token uma vez e tenta de novo
            }
            return response;
        }
    }

    private static async Task ThrowForStatus(HttpResponseMessage response, CancellationToken cancel)
    {
        if (response.IsSuccessStatusCode) return;
        var body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
        }
        var status = response.StatusCode;
        var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 15;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized => new AuthRequiredException("A sessao expirou. Entre de novo."),
            HttpStatusCode.Forbidden => new NotAuthorizedException("Sua conta nao tem permissao para usar a biblioteca. Fale com o administrador."),
            HttpStatusCode.NotFound => new FamilyNotOnServerException("O arquivo nao esta no servidor."),
            HttpStatusCode.TooManyRequests => new RateLimitedException((int)Math.Ceiling(retry), ExtractMessage(body) ?? "Limite de downloads atingido."),
            _ => new ServerUnavailableException($"O servidor respondeu {(int)status}."),
        };
    }

    private static string? ExtractMessage(string body)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(body)?["error"]?.GetValue<string>();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<byte[]> GetCatalogFileAsync(string name, CancellationToken cancel)
    {
        using var response = await GetAsync("/v1/catalog/" + Uri.EscapeDataString(name), null, cancel).ConfigureAwait(false);
        await ThrowForStatus(response, cancel).ConfigureAwait(false);
        try
        {
            return await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new ServerUnavailableException($"A conexao caiu durante o download: {error.Message}");
        }
    }

    /// <summary>
    /// Baixa a familia para <paramref name="partPath"/> (acrescenta se o arquivo ja tem bytes: retomada por Range).
    /// Devolve os bytes totais do arquivo parcial no fim.
    /// </summary>
    public async Task<long> DownloadFamilyAsync(string sha, string partPath, IProgress<(long done, long total)>? progress, CancellationToken cancel)
    {
        if (sha.Length != 64 || sha.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("SHA-256 invalido", nameof(sha));
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        using var response = await GetAsync("/v1/rfa/" + sha.ToLowerInvariant(), existing, cancel).ConfigureAwait(false);
        await ThrowForStatus(response, cancel).ConfigureAwait(false);

        var resumed = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        var length = response.Content.Headers.ContentLength ?? -1;
        var total = length < 0 ? -1 : length + (resumed ? existing : 0);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var file = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            var done = resumed ? existing : 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                progress?.Report((done, total));
            }
            return done;
        }
        catch (HttpRequestException error)
        {
            throw new ServerUnavailableException($"A conexao caiu durante o download: {error.Message}");
        }
        catch (IOException error) when (error.InnerException is not null && !cancel.IsCancellationRequested)
        {
            throw new ServerUnavailableException($"A conexao caiu durante o download: {error.InnerException.Message}");
        }
    }
}
