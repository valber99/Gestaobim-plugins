using System.Globalization;
using System.Text;

namespace Gestaobim.DwgExport.Core;

public enum NamePattern
{
    /// <summary>"A101 - Planta baixa".</summary>
    NumberAndName,

    /// <summary>"A101".</summary>
    NumberOnly,
}

/// <summary>File names for exported sheets: safe on Windows, without accents, unique within one export.</summary>
public static class FileNamer
{
    /// <summary>Windows MAX_PATH minus the terminating character. Longer paths fail in many CAD programs.</summary>
    public const int MaxPath = 259;

    public const string EmptyName = "Export";

    private static readonly HashSet<char> InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Removes accents, replaces characters Windows forbids with an underscore and never returns an empty name.</summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return EmptyName;

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(InvalidChars.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        }

        var clean = builder.ToString().Normalize(NormalizationForm.FormC).Trim().TrimEnd('.', ' ');
        return clean.Length == 0 ? EmptyName : clean;
    }

    public static string ForSheet(string? number, string? name, NamePattern pattern)
    {
        var numberPart = Sanitize(number);
        if (pattern == NamePattern.NumberOnly || string.IsNullOrWhiteSpace(name)) return numberPart;
        return $"{numberPart} - {Sanitize(name)}";
    }

    /// <summary>Returns <paramref name="name"/>, or "name_2", "name_3"... when it (ignoring case) is already used. Records the result.</summary>
    public static string Unique(string name, ISet<string> used)
    {
        var candidate = name;
        for (var counter = 2; used.Contains(candidate.ToLowerInvariant()); counter++)
            candidate = $"{name}_{counter}";
        used.Add(candidate.ToLowerInvariant());
        return candidate;
    }

    /// <summary>The names whose full path (folder + name + extension) would exceed <see cref="MaxPath"/>.</summary>
    public static IReadOnlyList<string> TooLong(string folder, IEnumerable<string> names, string extension)
    {
        var root = folder.TrimEnd('\\', '/');
        return names.Where(name => root.Length + 1 + name.Length + extension.Length > MaxPath).ToList();
    }
}

/// <summary>Orders "A2" before "A10": digit runs compare as numbers, everything else ignores case.</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var runX = x.AsSpan(startX, i - startX).TrimStart('0');
                var runY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (runX.Length != runY.Length) return runX.Length.CompareTo(runY.Length);
                var byDigits = runX.CompareTo(runY, StringComparison.Ordinal);
                if (byDigits != 0) return byDigits;
            }
            else
            {
                var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (byChar != 0) return byChar;
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
