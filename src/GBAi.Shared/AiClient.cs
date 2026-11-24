using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Gestaobim.Ai;

public sealed class AiException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Uma imagem a enviar ao modelo, com um rotulo que o prompt cita ("clay", "mask", "ref_1"...).</summary>
public sealed record ImagePart(string Label, string MimeType, byte[] Bytes);

/// <summary>O resultado de uma geracao de imagem.</summary>
public sealed record ImageResult(byte[] Bytes, string MimeType, string? Text);

/// <summary>O que os plugins precisam da IA. O resto (provedor, login, conta) e problema do gateway.</summary>
public interface IAiGateway
{
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancel = default);

    /// <summary>Texto para texto (planejar). Devolve o conteudo da resposta.</summary>
    Task<string> ChatAsync(string model, string system, string user, CancellationToken cancel = default);

    /// <summary>Imagens e texto para uma imagem (render, edicao).</summary>
    Task<ImageResult> GenerateImageAsync(string model, string prompt, IReadOnlyList<ImagePart> images, string? aspectRatio, string? imageSize, CancellationToken cancel = default);
}

/// <summary>Fala com o gateway local (CLIProxyAPI) pelas APIs compativeis com OpenAI e Gemini, sempre com o Bearer da sessao.</summary>
public sealed class AiClient : IAiGateway, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _token;

    public AiClient(GatewayEndpoint endpoint, TimeSpan? timeout = null)
        : this(endpoint.BaseUrl, endpoint.ClientToken, timeout)
    {
    }

    public AiClient(string baseUrl, string clientToken, TimeSpan? timeout = null)
    {
        _token = clientToken;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = timeout ?? TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientToken);
        _http.DefaultRequestHeaders.Add("x-goog-api-key", clientToken);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancel = default)
    {
        var node = await SendAsync(HttpMethod.Get, "/v1/models", null, cancel).ConfigureAwait(false);
        return (node?["data"] as JsonArray)?.Select(m => m?["id"]?.GetValue<string>()).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList()
               ?? [];
    }

    public async Task<string> ChatAsync(string model, string system, string user, CancellationToken cancel = default)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        var node = await SendAsync(HttpMethod.Post, "/v1/chat/completions", body, cancel).ConfigureAwait(false);
        var content = node?["choices"]?[0]?["message"]?["content"];
        var text = content is JsonValue v && v.TryGetValue<string>(out var s) ? s : content?.ToString();
        if (string.IsNullOrWhiteSpace(text)) throw new AiException("O modelo devolveu uma resposta vazia.");
        return text;
    }

    public async Task<ImageResult> GenerateImageAsync(string model, string prompt, IReadOnlyList<ImagePart> images, string? aspectRatio, string? imageSize, CancellationToken cancel = default)
    {
        var parts = new JsonArray();
        foreach (var image in images)
        {
            parts.Add(new JsonObject { ["text"] = $"[{image.Label}]" });
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject { ["mimeType"] = image.MimeType, ["data"] = Convert.ToBase64String(image.Bytes) },
            });
        }
        parts.Add(new JsonObject { ["text"] = prompt });

        var generation = new JsonObject { ["responseModalities"] = new JsonArray("TEXT", "IMAGE") };
        var imageConfig = new JsonObject();
        if (!string.IsNullOrEmpty(aspectRatio)) imageConfig["aspectRatio"] = aspectRatio;
        if (!string.IsNullOrEmpty(imageSize)) imageConfig["imageSize"] = imageSize;
        if (imageConfig.Count > 0) generation["imageConfig"] = imageConfig;

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = parts }),
            ["generationConfig"] = generation,
        };
        var node = await SendAsync(HttpMethod.Post, $"/v1beta/models/{Uri.EscapeDataString(model)}:generateContent", body, cancel).ConfigureAwait(false);
        return ParseImage(node);
    }

    /// <summary>Extrai a primeira imagem (e o texto) de uma resposta do generateContent.</summary>
    public static ImageResult ParseImage(JsonNode? response)
    {
        var text = new StringBuilder();
        byte[]? bytes = null;
        var mime = "image/png";
        foreach (var candidate in response?["candidates"] as JsonArray ?? [])
        {
            foreach (var part in candidate?["content"]?["parts"] as JsonArray ?? [])
            {
                if (part?["text"] is JsonNode t) text.Append(t.ToString());
                var inline = part?["inlineData"] ?? part?["inline_data"];
                if (bytes is null && inline?["data"] is JsonNode data)
                {
                    try
                    {
                        bytes = Convert.FromBase64String(data.GetValue<string>());
                    }
                    catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                    {
                        throw new AiException("O modelo devolveu uma imagem em base64 invalida.");
                    }
                    mime = (inline["mimeType"] ?? inline["mime_type"])?.GetValue<string>() ?? mime;
                }
            }
        }
        if (bytes is null)
        {
            var reason = response?["promptFeedback"]?["blockReason"]?.ToString();
            throw new AiException(reason is null
                ? "O modelo nao devolveu imagem." + (text.Length > 0 ? " Resposta: " + Redactor.Shorten(text.ToString(), 300) : "")
                : $"O modelo recusou o pedido ({reason}).");
        }
        return new ImageResult(bytes, mime, text.Length > 0 ? text.ToString() : null);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new AiException("Nao foi possivel falar com o gateway de IA: " + Redactor.Scrub(error.Message, _token));
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new AiException("O gateway de IA demorou demais para responder.");
        }
        using (response)
        {
            string text;
            try
            {
                text = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new AiException("O gateway de IA demorou demais para concluir a resposta.");
            }
            if (!response.IsSuccessStatusCode)
                throw new AiException($"O gateway respondeu {(int)response.StatusCode}: {Redactor.Shorten(Redactor.Scrub(ErrorMessage(text), _token), 400)}", (int)response.StatusCode);
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                throw new AiException("O gateway de IA devolveu uma resposta JSON invalida.");
            }
        }
    }

    private static string ErrorMessage(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return node?["error"]?["message"]?.ToString() ?? node?["error"]?.ToString() ?? node?["message"]?.ToString() ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    public void Dispose() => _http.Dispose();
}
