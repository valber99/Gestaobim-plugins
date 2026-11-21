using System.Text.RegularExpressions;

namespace Gestaobim.MaterialStudio.Core;

public enum CaseMode { Keep, Title, Upper, Lower, Sentence }

public enum RenameStatus { Ok, Unchanged, Empty, Conflict }

/// <summary>How the names of a batch are transformed.</summary>
public sealed class RenameRules
{
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public bool IgnoreCase { get; init; } = true;
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
    public CaseMode Case { get; init; } = CaseMode.Keep;
    public bool CollapseSpaces { get; init; } = true;
}

public sealed record RenameStep(long Key, string Old, string New, RenameStatus Status);

public static class Renamer
{
    private static readonly HashSet<string> LowerWords = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "e", "em", "com", "para", "a", "o",
    };

    /// <summary>Applies the rules to one name. Throws <see cref="ArgumentException"/> for an invalid regular expression.</summary>
    public static string Apply(string? name, RenameRules rules)
    {
        var text = name ?? "";
        if (rules.Find.Length > 0)
        {
            var options = rules.IgnoreCase ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant : RegexOptions.CultureInvariant;
            try
            {
                if (rules.UseRegex)
                {
                    // Python-style \1 references are what users type; .NET wants ${1}.
                    var replacement = Regex.Replace(rules.Replace, @"\\(\d+)", "$${$1}");
                    text = Regex.Replace(text, rules.Find, replacement, options);
                }
                else
                {
                    var literal = rules.Replace;
                    text = Regex.Replace(text, Regex.Escape(rules.Find), _ => literal, options);
                }
            }
            catch (RegexParseException error)
            {
                throw new ArgumentException($"Expressao regular invalida: {error.Message}", nameof(rules), error);
            }
        }
        if (rules.CollapseSpaces)
            text = Regex.Replace(text, @"\s+", " ");
        text = ApplyCase(text.Trim(), rules.Case);
        return rules.Prefix + text + rules.Suffix;
    }

    private static string ApplyCase(string text, CaseMode mode) => mode switch
    {
        CaseMode.Upper => text.ToUpperInvariant(),
        CaseMode.Lower => text.ToLowerInvariant(),
        CaseMode.Title => Title(text),
        CaseMode.Sentence => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant(),
        _ => text,
    };

    private static string Title(string text)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var lowered = words[i].ToLowerInvariant();
            words[i] = i > 0 && LowerWords.Contains(lowered)
                ? lowered
                : lowered.Length == 0 ? lowered : char.ToUpperInvariant(lowered[0]) + lowered[1..];
        }
        return string.Join(' ', words);
    }

    /// <summary>
    /// Plans a batch rename. A name is a conflict when another material outside the batch already
    /// uses it, or when two renamed items would end up with the same name. Comparison ignores case.
    /// Names freed by the batch can be reused, so swaps and chains are valid; apply them in two phases.
    /// </summary>
    public static List<RenameStep> Plan(IReadOnlyList<(long Key, string Old)> items, RenameRules rules, IEnumerable<string> allNames)
    {
        var moving = new HashSet<string>(items.Select(i => i.Old), StringComparer.OrdinalIgnoreCase);
        var occupied = new HashSet<string>(allNames, StringComparer.OrdinalIgnoreCase);
        occupied.ExceptWith(moving);

        var proposed = items.Select(i => (i.Key, i.Old, New: Apply(i.Old, rules))).ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in proposed)
            counts[p.New] = counts.GetValueOrDefault(p.New) + 1;

        var steps = new List<RenameStep>(proposed.Count);
        foreach (var (key, old, next) in proposed)
        {
            RenameStatus status;
            if (string.IsNullOrWhiteSpace(next)) status = RenameStatus.Empty;
            else if (next == old) status = RenameStatus.Unchanged;
            else if (occupied.Contains(next) || counts[next] > 1) status = RenameStatus.Conflict;
            else status = RenameStatus.Ok;
            steps.Add(new RenameStep(key, old, next, status));
        }
        return steps;
    }
}

public sealed record Candidate(long Key, string Name, string Signature, int Usage);

public sealed record DuplicateGroup(Candidate Primary, IReadOnlyList<Candidate> Duplicates);

public static class DuplicateFinder
{
    /// <summary>
    /// Groups candidates that share a signature. The primary of a group is the most used one; ties go to
    /// the shortest name, then alphabetical order, so the result is stable.
    /// </summary>
    public static List<DuplicateGroup> Group(IEnumerable<Candidate> candidates)
    {
        var groups = new List<DuplicateGroup>();
        foreach (var bucket in candidates.GroupBy(c => c.Signature))
        {
            var members = bucket.ToList();
            if (members.Count < 2) continue;
            var ordered = members
                .OrderByDescending(c => c.Usage)
                .ThenBy(c => c.Name.Length)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            groups.Add(new DuplicateGroup(ordered[0], ordered.Skip(1).ToList()));
        }
        groups.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Primary.Name, b.Primary.Name));
        return groups;
    }
}
