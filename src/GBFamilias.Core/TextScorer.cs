namespace Gestaobim.Familias.Core;

/// <summary>Busca textual: todas as palavras precisam aparecer; o peso depende de onde apareceram.</summary>
public static class TextScorer
{
    /// <summary>0 quando alguma palavra nao aparece em lugar nenhum; senao, uma pontuacao maior = mais relevante.</summary>
    public static double Score(FamilyRecord r, string[] tokens)
    {
        if (tokens.Length == 0) return 1;
        double total = 0;
        foreach (var t in tokens)
        {
            double best = 0;
            if (r.FoldedName.Contains(t)) best = WordStart(r.FoldedName, t) ? 13 : 10;
            foreach (var tag in r.FoldedTags)
            {
                if (tag.Contains(t)) { best = Math.Max(best, WordStart(tag, t) ? 8 : 6); break; }
            }
            if (best < 5 && (r.FoldedCategory.Contains(t) || r.FoldedSub.Contains(t))) best = 5;
            if (best < 3 && (Any(r.FoldedMaterials, t) || Any(r.FoldedColors, t)
                             || Any(r.FoldedEnvironments, t) || r.FoldedStyle.Contains(t))) best = 3;
            if (best < 2 && r.FoldedFolder.Contains(t)) best = 2;
            if (best < 1 && r.FoldedBlob.Contains(t)) best = 1;
            if (best == 0) return 0;
            total += best;
        }
        return total;
    }

    private static bool Any(string[] values, string token)
    {
        foreach (var v in values) if (v.Contains(token)) return true;
        return false;
    }

    private static bool WordStart(string text, string token) =>
        text.StartsWith(token, StringComparison.Ordinal) || text.Contains(' ' + token, StringComparison.Ordinal);
}

/// <summary>Une listas ja ordenadas (texto, busca semantica) pela fusao de ranks (RRF), como faz o gbfam.</summary>
public static class Ranker
{
    public static List<long> Merge(params (double Weight, IReadOnlyList<long> Ids)[] lists)
    {
        var score = new Dictionary<long, double>();
        foreach (var (weight, ids) in lists)
            for (var rank = 0; rank < ids.Count; rank++)
                score[ids[rank]] = score.GetValueOrDefault(ids[rank]) + weight / (60.0 + rank + 1);
        return score.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToList();
    }
}
