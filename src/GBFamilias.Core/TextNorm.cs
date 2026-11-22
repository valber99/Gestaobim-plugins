using System.Globalization;
using System.Text;

namespace Gestaobim.Familias.Core;

/// <summary>Texto sem acento, minusculo e com espacos simples: base de toda comparacao e busca.</summary>
public static class TextNorm
{
    public static string Fold(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c) || c == '_')
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Palavras vazias do portugues: exigir que "de" apareca so atrapalharia a busca.</summary>
    private static readonly HashSet<string> StopWords =
        ["de", "da", "do", "das", "dos", "e", "a", "o", "as", "os", "em", "no", "na", "nos", "nas", "com", "para", "por", "um", "uma", "ou"];

    /// <summary>Palavras de uma consulta, ja sem acento e sem palavras vazias. Ignora pontuacao.</summary>
    public static string[] Tokens(string? text) =>
        Fold(text).Split([' ', ',', ';', '.', '-', '/', '\\', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !StopWords.Contains(t)).ToArray();

    /// <summary>"cinza claro" vira "Cinza claro".</summary>
    public static string Capitalize(string text)
    {
        var t = text.Trim();
        return t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];
    }
}
