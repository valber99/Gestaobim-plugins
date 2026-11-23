using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gestaobim.Ai;

/// <summary>Limpa texto antes de ir para log, relatorio ou mensagem de erro: sem chave, sem token, sem imagem em base64.</summary>
public static class Redactor
{
    private static readonly Regex Base64Blob = new(@"[A-Za-z0-9+/=]{200,}", RegexOptions.Compiled);
    private static readonly Regex Secret = new(
        @"(sk-[A-Za-z0-9_\-]{16,}|AIza[0-9A-Za-z_\-]{20,}|Bearer\s+[A-Za-z0-9._\-]{12,}|eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]*)",
        RegexOptions.Compiled);

    public static string Scrub(string text, params string?[] secrets)
    {
        foreach (var s in secrets.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 6))
            text = text.Replace(s!, "[segredo]", StringComparison.Ordinal);
        text = Secret.Replace(text, "[segredo]");
        return Base64Blob.Replace(text, m => $"[dados {m.Length} caracteres]");
    }

    public static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    /// <summary>
    /// O corpo de um pedido para o registro de auditoria: as imagens viram "[imagem image/png N bytes]" e nenhum token entra.
    /// </summary>
    public static string DescribeRequest(JsonNode body)
    {
        var clone = JsonNode.Parse(body.ToJsonString())!;
        Walk(clone);
        return Scrub(clone.ToJsonString());
    }

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (key is "data" && obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 200)
                        obj[key] = $"[imagem {s.Length * 3 / 4} bytes]";
                    else Walk(obj[key]);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) Walk(item);
                break;
        }
    }
}

/// <summary>Tira um objeto JSON de uma resposta de modelo (que pode vir com texto antes, depois ou dentro de uma cerca ```).</summary>
public static class JsonExtract
{
    public static string? FirstObject(string text)
    {
        var start = text.IndexOf('{');
        while (start >= 0)
        {
            var depth = 0;
            var inString = false;
            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    var candidate = text.Substring(start, i - start + 1);
                    try
                    {
                        System.Text.Json.JsonDocument.Parse(candidate).Dispose();
                        return candidate;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        break;
                    }
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }
}
