using System;
using System.Collections.Generic;
using System.Linq;

namespace Gestaobim.Ai;

/// <summary>
/// Leitor minimo de YAML so para conferir o config.yaml que o proprio plugin gera: mapas aninhados, valores simples e
/// listas de valores simples. Os caminhos saem como "server.host" e "access.api-keys[0]". Nao e um parser geral.
/// </summary>
public static class MiniYaml
{
    public static Dictionary<string, string> Flatten(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new List<(int Indent, string Key)>();
        var listIndex = new Dictionary<string, int>();
        foreach (var raw in yaml.Replace("\r", "").Split('\n'))
        {
            var line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line)) continue;
            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var parent = string.Join(".", stack.Select(s => s.Key));

            if (text.StartsWith("- ", StringComparison.Ordinal) || text == "-")
            {
                var item = text.Length > 1 ? text[1..].Trim() : "";
                var index = listIndex.GetValueOrDefault(parent);
                listIndex[parent] = index + 1;
                var path = $"{parent}[{index}]";
                var colon = FindKeyColon(item);
                if (colon > 0)
                {
                    // "- chave: valor": abre um mapa dentro da lista (usado so nos grupos de chaves de provedores).
                    var key = Unquote(item[..colon].Trim());
                    var value = item[(colon + 1)..].Trim();
                    stack.Add((indent, parent.Length == 0 ? $"[{index}]" : $"{parent.Split('.')[^1]}[{index}]"));
                    var itemPath = string.Join(".", stack.Select(s => s.Key));
                    if (value.Length > 0) result[$"{itemPath}.{key}"] = Unquote(value);
                    else stack.Add((indent + 2, key));
                }
                else
                {
                    result[path] = Unquote(item);
                }
                continue;
            }

            var c = FindKeyColon(text);
            if (c <= 0) continue;
            var name = Unquote(text[..c].Trim());
            var rest = text[(c + 1)..].Trim();
            if (rest.Length == 0)
            {
                stack.Add((indent, name));
            }
            else
            {
                result[parent.Length == 0 ? name : $"{parent}.{name}"] = Unquote(rest);
            }
        }
        return result;
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inQuote = !inQuote;
            else if (line[i] == '#' && !inQuote && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line[..i];
        }
        return line;
    }

    private static int FindKeyColon(string text)
    {
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') inQuote = !inQuote;
            else if (text[i] == ':' && !inQuote && (i == text.Length - 1 || text[i + 1] == ' ')) return i;
        }
        return -1;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return value;
    }
}
