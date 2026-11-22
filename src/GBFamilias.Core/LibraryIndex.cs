namespace Gestaobim.Familias.Core;

/// <summary>
/// A biblioteca inteira em memoria (so metadados: alguns MB para milhares de familias). Filtros, facetas e busca
/// textual sao instantaneos e nao dependem do banco nem de servidor.
/// </summary>
public sealed class LibraryIndex
{
    private readonly Dictionary<long, FamilyRecord> _byId = [];
    private readonly Dictionary<string, FamilyRecord> _bySha = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Facet, Dictionary<string, string>> _labels = [];

    public IReadOnlyList<FamilyRecord> All { get; }

    public LibraryIndex(IEnumerable<FamilyRecord> records)
    {
        All = records.ToList();
        CategoryNormalizer.Apply(All);
        foreach (var r in All)
        {
            r.Prepare();
            _byId[r.Id] = r;
            if (r.Sha.Length > 0) _bySha[r.Sha] = r;
        }
        _labels[Facet.Category] = CategoryNormalizer.Labels(All.Select(r => r.Category), CategoryNormalizer.Key);
        _labels[Facet.Folder] = CategoryNormalizer.Labels(All.Select(r => r.FolderCategory), TextNorm.Fold);
        _labels[Facet.Environment] = CategoryNormalizer.Labels(All.SelectMany(r => r.Environments), TextNorm.Fold);
        _labels[Facet.Material] = CategoryNormalizer.Labels(All.SelectMany(r => r.Materials), TextNorm.Fold);
        _labels[Facet.Color] = CategoryNormalizer.Labels(All.SelectMany(r => r.Colors), TextNorm.Fold);
        _labels[Facet.Style] = CategoryNormalizer.Labels(All.Select(r => r.Style), TextNorm.Fold);
        _labels[Facet.RevitCategory] = CategoryNormalizer.Labels(All.Select(r => r.RevitCategory), TextNorm.Fold);
        _labels[Facet.RevitVersion] = All.Select(r => r.RevitVersion).Where(v => !string.IsNullOrEmpty(v)).Distinct()
            .ToDictionary(v => v!, v => v!.StartsWith('~') ? $"{v[1..]} (estimada)" : v!);
    }

    public FamilyRecord? ById(long id) => _byId.GetValueOrDefault(id);
    public FamilyRecord? BySha(string sha) => _bySha.GetValueOrDefault(sha);

    /// <summary>Rotulo de exibicao de uma chave de faceta.</summary>
    public string Label(Facet facet, string key) => _labels[facet].GetValueOrDefault(key) ?? key;

    private static IEnumerable<string> Keys(FamilyRecord r, Facet facet) => facet switch
    {
        Facet.Category => r.CategoryKey.Length > 0 ? [r.CategoryKey] : [],
        Facet.Folder => r.FolderCategory is { Length: > 0 } ? [TextNorm.Fold(r.FolderCategory)] : [],
        Facet.Environment => r.FoldedEnvironments,
        Facet.Material => r.FoldedMaterials,
        Facet.Color => r.FoldedColors,
        Facet.Style => r.FoldedStyle.Length > 0 ? [r.FoldedStyle] : [],
        Facet.RevitVersion => r.RevitVersion is { Length: > 0 } ? [r.RevitVersion] : [],
        Facet.RevitCategory => TextNorm.Fold(r.RevitCategory) is { Length: > 0 } k ? [k] : [],
        _ => [],
    };

    /// <summary>Filtros que nao pertencem a nenhum grupo de faceta (preview, lista de hashes, filtro extra).</summary>
    private static bool PassesGlobal(FamilyRecord r, FilterSpec spec) =>
        (!spec.OnlyWithPreview || r.HasPreview)
        && (spec.OnlyShas is null || spec.OnlyShas.Contains(r.Sha))
        && (spec.Extra is null || spec.Extra(r));

    /// <summary>A familia atende ao grupo de filtros de uma faceta? (Sem selecao no grupo, sempre atende.)</summary>
    private static bool MatchesFacet(FamilyRecord r, FilterSpec spec, Facet facet)
    {
        if (facet == Facet.Category)
        {
            // Categoria e subcategoria formam um grupo so: marcar a subcategoria tambem libera a categoria dela.
            return spec.Categories.Count + spec.Subcategories.Count == 0
                   || spec.Categories.Contains(r.CategoryKey) || spec.Subcategories.Contains(r.SubcategoryKey);
        }
        var selected = spec.Selection(facet);
        return selected.Count == 0 || Keys(r, facet).Any(selected.Contains);
    }

    private static readonly Facet[] AllFacets = Enum.GetValues<Facet>();

    /// <summary>Atende a todos os filtros, menos o texto (e menos o grupo <paramref name="skip"/>, usado nas contagens).</summary>
    private static bool MatchesFacets(FamilyRecord r, FilterSpec spec, Facet? skip)
    {
        if (!PassesGlobal(r, spec)) return false;
        foreach (var facet in AllFacets)
        {
            if (facet == skip) continue;
            if (!MatchesFacet(r, spec, facet)) return false;
        }
        return true;
    }

    /// <summary>Resultado dos filtros e do texto. <paramref name="semantic"/> traz os ids da busca por IA, ja em ordem.</summary>
    public IReadOnlyList<FamilyRecord> Query(FilterSpec spec, SortMode sort = SortMode.Relevance, IReadOnlyList<long>? semantic = null)
    {
        var tokens = TextNorm.Tokens(spec.Text);
        var scored = new List<(FamilyRecord Record, double Score)>();
        foreach (var r in All)
        {
            if (!MatchesFacets(r, spec, null)) continue;
            var s = TextScorer.Score(r, tokens);
            if (s > 0) scored.Add((r, s));
        }

        var textual = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Record.FoldedName, StringComparer.Ordinal)
            .Select(x => x.Record).ToList();

        if (semantic is { Count: > 0 })
        {
            // So entra o que passa nos filtros; o texto nao restringe a busca por IA, ele soma.
            var semanticIds = semantic.Where(id => _byId.TryGetValue(id, out var r) && MatchesFacets(r, spec, null)).ToList();
            var textIds = tokens.Length > 0 ? textual.Select(r => r.Id).ToList() : [];
            var merged = Ranker.Merge((1.0, textIds), (1.0, semanticIds));
            var result = merged.Select(id => _byId[id]).ToList();
            return sort == SortMode.Relevance ? result : Sort(result, sort);
        }

        return sort == SortMode.Relevance
            ? (tokens.Length > 0 ? textual : Sort(textual, SortMode.Name))
            : Sort(textual, sort);
    }

    private static List<FamilyRecord> Sort(List<FamilyRecord> list, SortMode sort) => sort switch
    {
        SortMode.Category => list.OrderBy(r => r.FoldedCategory, StringComparer.Ordinal).ThenBy(r => r.FoldedName, StringComparer.Ordinal).ToList(),
        _ => list.OrderBy(r => r.FoldedName, StringComparer.Ordinal).ToList(),
    };

    /// <summary>Contagens de uma faceta considerando todos os OUTROS filtros (o proprio grupo nao se exclui).</summary>
    public IReadOnlyList<FacetCount> Counts(FilterSpec spec, Facet facet)
    {
        var tokens = TextNorm.Tokens(spec.Text);
        var counts = new Dictionary<string, int>();
        foreach (var r in All)
        {
            if (!MatchesFacets(r, spec, facet)) continue;
            if (tokens.Length > 0 && TextScorer.Score(r, tokens) <= 0) continue;
            foreach (var key in Keys(r, facet).Distinct())
                counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts
            .Select(kv => new FacetCount(kv.Key, Label(facet, kv.Key), kv.Value))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// As contagens de TODAS as facetas numa passada so (o que a janela precisa a cada filtro). Mesmo resultado de chamar
    /// <see cref="Counts"/> para cada faceta, mas lê cada familia uma vez: uma familia que so falha num grupo conta
    /// apenas nesse grupo; uma que passa em todos conta em todos.
    /// </summary>
    public IReadOnlyDictionary<Facet, IReadOnlyList<FacetCount>> CountsAll(FilterSpec spec)
    {
        var tokens = TextNorm.Tokens(spec.Text);
        var maps = AllFacets.ToDictionary(f => f, _ => new Dictionary<string, int>());
        foreach (var r in All)
        {
            if (!PassesGlobal(r, spec)) continue;
            Facet? failed = null;
            var failures = 0;
            foreach (var facet in AllFacets)
            {
                if (MatchesFacet(r, spec, facet)) continue;
                failed = facet;
                if (++failures > 1) break;
            }
            if (failures > 1) continue;
            if (tokens.Length > 0 && TextScorer.Score(r, tokens) <= 0) continue;
            foreach (var facet in failures == 0 ? AllFacets : [failed!.Value])
            {
                var counts = maps[facet];
                foreach (var key in Keys(r, facet).Distinct()) counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        return AllFacets.ToDictionary(f => f, f => (IReadOnlyList<FacetCount>)maps[f]
            .Select(kv => new FacetCount(kv.Key, Label(f, kv.Key), kv.Value))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    /// <summary>Subcategorias de uma categoria, com contagem (chave "categoria|subcategoria").</summary>
    public IReadOnlyList<FacetCount> Subcategories(FilterSpec spec, string categoryKey)
    {
        var tokens = TextNorm.Tokens(spec.Text);
        var counts = new Dictionary<string, (string Label, int Count)>();
        foreach (var r in All)
        {
            if (r.CategoryKey != categoryKey || r.SubcategoryKey.Length == 0) continue;
            if (!MatchesFacets(r, spec, Facet.Category)) continue;
            if (tokens.Length > 0 && TextScorer.Score(r, tokens) <= 0) continue;
            var cur = counts.GetValueOrDefault(r.SubcategoryKey);
            counts[r.SubcategoryKey] = (r.Subcategory ?? r.SubcategoryKey, cur.Count + 1);
        }
        return counts
            .Select(kv => new FacetCount(kv.Key, kv.Value.Label, kv.Value.Count))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

