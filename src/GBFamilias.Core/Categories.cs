namespace Gestaobim.Familias.Core;

/// <summary>
/// A IA escreve "Veiculo" e "Veiculos", "Iluminacao" e "Iluminação". Aqui as variantes viram uma chave so
/// (sem acento, minuscula, sem "s" final) e o rotulo exibido e a forma mais frequente.
/// </summary>
public static class CategoryNormalizer
{
    public static string Key(string? value)
    {
        var k = TextNorm.Fold(value);
        if (k.Length > 4 && k.EndsWith('s') && !k.EndsWith("ss")) k = k[..^1];
        return k;
    }

    /// <summary>Preenche CategoryKey/Category e SubcategoryKey/Subcategory de todos os registros.</summary>
    public static void Apply(IReadOnlyList<FamilyRecord> records)
    {
        var catLabels = Labels(records.Select(r => r.Category), Key);
        var subLabels = Labels(records.Select(r => r.Subcategory), Key);
        foreach (var r in records)
        {
            var ck = Key(r.Category);
            r.CategoryKey = ck;
            r.Category = ck.Length == 0 ? null : catLabels[ck];
            var sk = Key(r.Subcategory);
            r.SubcategoryKey = sk.Length == 0 ? "" : ck + "|" + sk;
            r.Subcategory = sk.Length == 0 ? null : subLabels[sk];
        }
    }

    /// <summary>chave -> forma mais frequente (empate: a mais curta, que costuma ser a no singular).</summary>
    public static Dictionary<string, string> Labels(IEnumerable<string?> values, Func<string?, string> key)
    {
        return values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .GroupBy(key)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(
                g => g.Key,
                g => TextNorm.Capitalize(g.GroupBy(x => x).OrderByDescending(x => x.Count()).ThenBy(x => x.Key.Length).First().Key));
    }
}
