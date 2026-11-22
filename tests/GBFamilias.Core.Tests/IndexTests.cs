using Gestaobim.Familias.Core;
using static GBFamilias.Core.Tests.TestSupport;

namespace GBFamilias.Core.Tests;

public class TextAndCategoryTests
{
    [Theory]
    [InlineData("Lavatório", "lavatorio")]
    [InlineData("  Cinza   Claro ", "cinza claro")]
    [InlineData("Vaso_de_Planta", "vaso de planta")]
    [InlineData(null, "")]
    public void FoldRemovesAccentsCaseAndExtraSpaces(string? input, string expected) =>
        Assert.Equal(expected, TextNorm.Fold(input));

    [Fact]
    public void TokensIgnorePunctuationAndStopWords() =>
        Assert.Equal(["cadeira", "escritorio"], TextNorm.Tokens("Cadeira, de-escritório"));

    [Fact]
    public void StopWordsDoNotRestrictTheSearch()
    {
        var index = SampleIndex();
        // "de" nao pode exigir nada: "vaso de planta" acha o vaso mesmo que a palavra "de" so apareca no nome.
        Assert.Equal(index.Query(new FilterSpec { Text = "lavatorio banheiro" }).Select(r => r.Id),
                     index.Query(new FilterSpec { Text = "lavatorio de banheiro" }).Select(r => r.Id));
        // Uma consulta so de palavras vazias nao filtra nada.
        Assert.Equal(index.All.Count, index.Query(new FilterSpec { Text = "de da o" }).Count);
    }

    [Fact]
    public void PluralAndAccentVariantsShareOneCategoryKey()
    {
        Assert.Equal(CategoryNormalizer.Key("Veículo"), CategoryNormalizer.Key("Veiculos"));
        Assert.NotEqual(CategoryNormalizer.Key("Portas"), CategoryNormalizer.Key("Janelas"));
        Assert.Equal("gas", CategoryNormalizer.Key("Gas"));
    }

    [Fact]
    public void CategoryLabelIsTheMostFrequentForm()
    {
        var index = new LibraryIndex(
        [
            Rec(1, "a", "Veículo"), Rec(2, "b", "Veículos"), Rec(3, "c", "Veículos"), Rec(4, "d", "Mobiliário"),
        ]);
        var cats = index.Counts(new FilterSpec(), Facet.Category);
        Assert.Equal(2, cats.Count);
        var vehicles = cats.Single(c => c.Count == 3);
        Assert.Equal("Veículos", vehicles.Label);
    }
}

public class QueryTests
{
    private static readonly LibraryIndex Index = SampleIndex();

    private static List<long> Ids(IEnumerable<FamilyRecord> records) => records.Select(r => r.Id).ToList();

    [Fact]
    public void EmptyFilterReturnsEverythingSortedByName()
    {
        var all = Index.Query(new FilterSpec());
        Assert.Equal(8, all.Count);
        Assert.Equal(all.OrderBy(r => TextNorm.Fold(r.Title + " " + r.Name), StringComparer.Ordinal).Select(r => r.Id), all.Select(r => r.Id));
    }

    [Fact]
    public void TextSearchIgnoresAccentsAndFindsTags()
    {
        Assert.Equal([4L], Ids(Index.Query(new FilterSpec { Text = "lavatorio" })));
        Assert.Equal([4L], Ids(Index.Query(new FilterSpec { Text = "LAVATÓRIO banheiro" })));
    }

    [Fact]
    public void AllWordsMustMatch()
    {
        Assert.Empty(Index.Query(new FilterSpec { Text = "sofa banheiro" }));
        Assert.Equal(2, Index.Query(new FilterSpec { Text = "sofa" }).Count);
    }

    [Fact]
    public void NameMatchOutranksDescriptionMatch()
    {
        var index = new LibraryIndex(
        [
            Rec(1, "Outra coisa", description: "tem uma mesa no texto"),
            Rec(2, "Mesa de jantar"),
        ]);
        Assert.Equal([2L, 1L], Ids(index.Query(new FilterSpec { Text = "mesa" })));
    }

    [Fact]
    public void FriendlyNameIsSearchable() =>
        Assert.Equal([1L], Ids(Index.Query(new FilterSpec { Text = "tres lugares" })));

    [Fact]
    public void FiltersCombineWithAndAcrossGroupsAndOrWithin()
    {
        var spec = new FilterSpec();
        spec.Environments.Add("sala");
        Assert.Equal(4, Index.Query(spec).Count); // sofa 56, sofa 55, mesa, vaso

        spec.Materials.Add("couro");
        spec.Materials.Add("madeira"); // ou: couro OU madeira
        Assert.Equal([2L, 3L], Ids(Index.Query(spec)).OrderBy(i => i).ToList());

        spec.Colors.Add("preto"); // e: preto
        Assert.Equal([2L], Ids(Index.Query(spec)));
    }

    [Fact]
    public void CategoryAndSubcategoryAreOneGroup()
    {
        var spec = new FilterSpec();
        spec.Subcategories.Add(Index.All.First(r => r.Id == 1).SubcategoryKey);
        Assert.Equal([1L, 2L], Ids(Index.Query(spec)).OrderBy(i => i).ToList());

        spec.Categories.Add(Index.All.First(r => r.Id == 4).CategoryKey); // soma Hidraulica
        Assert.Equal([1L, 2L, 4L], Ids(Index.Query(spec)).OrderBy(i => i).ToList());
    }

    [Fact]
    public void OnlyWithPreviewAndOnlyShas()
    {
        Assert.DoesNotContain(Index.Query(new FilterSpec { OnlyWithPreview = true }), r => r.Id == 5);
        var spec = new FilterSpec { OnlyShas = ["sha3", "sha4", "nao-existe"] };
        Assert.Equal([3L, 4L], Ids(Index.Query(spec)).OrderBy(i => i).ToList());
    }

    [Fact]
    public void FacetCountsDoNotExcludeTheirOwnGroup()
    {
        var spec = new FilterSpec();
        spec.Materials.Add("couro");
        // Com "couro" marcado, os outros materiais continuam listados (para poder trocar a escolha).
        var materials = Index.Counts(spec, Facet.Material);
        Assert.Contains(materials, m => m.Key == "madeira");
        // Ja os outros grupos respeitam o filtro: so o sofa 55 tem couro, e ele esta na SALA.
        var folders = Index.Counts(spec, Facet.Folder);
        Assert.Single(folders);
        Assert.Equal(1, folders[0].Count);
    }

    [Fact]
    public void FacetCountsFollowTheTextQuery()
    {
        var colors = Index.Counts(new FilterSpec { Text = "sofa" }, Facet.Color);
        Assert.Equal(["cinza", "preto"], colors.Select(c => c.Key).OrderBy(k => k).ToArray());
    }

    [Fact]
    public void SubcategoryCountsBelongToTheirCategory()
    {
        var cat = Index.All.First(r => r.Id == 1).CategoryKey;
        var subs = Index.Subcategories(new FilterSpec(), cat);
        Assert.Equal(["Mesas", "Sofás"], subs.Select(s => s.Label).OrderBy(s => s, StringComparer.Ordinal).ToArray());
        Assert.Equal(2, subs.Single(s => s.Label == "Sofás").Count);
    }

    [Fact]
    public void RevitVersionLabelsMarkEstimates()
    {
        Assert.Equal("2008 (estimada)", Index.Label(Facet.RevitVersion, "~2008"));
        Assert.Equal("2020", Index.Label(Facet.RevitVersion, "2020"));
    }

    [Fact]
    public void SortByCategoryGroupsThenOrdersByName()
    {
        var sorted = Index.Query(new FilterSpec(), SortMode.Category);
        // Sem categoria (o 8) vem primeiro; depois as categorias em ordem alfabetica sem acento.
        Assert.Equal(8, sorted[0].Id);
        var labels = sorted.Skip(1).Select(r => TextNorm.Fold(r.Category)).ToList();
        Assert.Equal(labels.OrderBy(c => c, StringComparer.Ordinal), labels);
        // Dentro da mesma categoria, por nome: Mesa 10 antes de Sofa 55 antes de Sofa 56.
        var furniture = sorted.Where(r => r.Category == "Mobiliário").Select(r => r.Id).ToList();
        Assert.Equal([3L, 2L, 1L], furniture);
    }

    [Fact]
    public void ClearAndCloneAreIndependent()
    {
        var spec = new FilterSpec { Text = "x", OnlyWithPreview = true };
        spec.Colors.Add("azul");
        var copy = spec.Clone();
        spec.Clear();
        Assert.True(spec.IsEmpty);
        Assert.False(copy.IsEmpty);
        Assert.Contains("azul", copy.Colors);
    }
}

public class SemanticMergeTests
{
    private static readonly LibraryIndex Index = SampleIndex();

    [Fact]
    public void RrfPutsItemsFoundByBothListsFirst()
    {
        var merged = Ranker.Merge((1.0, new List<long> { 1, 2, 3 }), (1.0, new List<long> { 3, 4 }));
        Assert.Equal(3, merged[0]);
        // 2 e 4 empatam (1/62 cada); o desempate e pelo id, entao o resultado e deterministico.
        Assert.Equal([3L, 1L, 2L, 4L], merged);
    }

    [Fact]
    public void SemanticOnlyKeepsItsOrderAndAppliesFilters()
    {
        var spec = new FilterSpec();
        spec.Folders.Add("sala");
        var result = Index.Query(spec, SortMode.Relevance, semantic: [4, 3, 1, 5]);
        Assert.Equal([3L, 1L], result.Select(r => r.Id).ToList()); // 4 e do WC e 5 e da DECOR: filtrados
    }

    [Fact]
    public void TextAndSemanticBlend()
    {
        // O texto acha o 4 (lavatorio); a IA acha 3 e 4. O 4 aparece por ter os dois sinais.
        var result = Index.Query(new FilterSpec { Text = "lavatorio" }, SortMode.Relevance, semantic: [3, 4]);
        Assert.Equal(4, result[0].Id);
        Assert.Contains(result, r => r.Id == 3);
    }

    [Fact]
    public void EmptySemanticListFallsBackToTextOnly()
    {
        var result = Index.Query(new FilterSpec { Text = "mesa" }, SortMode.Relevance, semantic: []);
        Assert.Equal([3L], result.Select(r => r.Id).ToList());
    }
}

public class CountsAllTests
{
    private static void AssertSame(LibraryIndex index, FilterSpec spec)
    {
        var all = index.CountsAll(spec);
        foreach (var facet in Enum.GetValues<Facet>())
            Assert.Equal(index.Counts(spec, facet), all[facet]);
    }

    [Fact]
    public void IgualACadaFacetaSeparadaComVariosFiltros()
    {
        var index = TestSupport.SampleIndex();
        AssertSame(index, new FilterSpec());
        AssertSame(index, new FilterSpec { Text = "sofa" });
        AssertSame(index, new FilterSpec { Text = "lavatorio de banheiro" });
        AssertSame(index, new FilterSpec { OnlyWithPreview = true });
        AssertSame(index, new FilterSpec { Extra = r => r.Id % 2 == 0 });

        var a = new FilterSpec();
        a.Environments.Add("sala");
        AssertSame(index, a);
        var b = new FilterSpec();
        b.Materials.Add("tecido");
        b.Environments.Add("sala");
        AssertSame(index, b);
        var c = new FilterSpec { Text = "mesa" };
        c.Categories.Add(index.All.First(r => r.Category == "Mobiliário").CategoryKey);
        c.Colors.Add("marrom");
        AssertSame(index, c);
        var d = new FilterSpec { OnlyShas = ["sha1", "sha3", "sha4"] };
        d.Folders.Add("sala");
        AssertSame(index, d);
    }

    [Fact]
    public void IgualEmUmaBibliotecaGrandeAleatoria()
    {
        var rng = new Random(11);
        string[] cats = ["Mobiliário", "Hidráulica", "Portas", "Janelas", "Vegetação"];
        string[] envs = ["sala", "cozinha", "banheiro", "quarto", "externo"];
        string[] mats = ["madeira", "vidro", "aço", "tecido", "cerâmica", "couro"];
        var records = Enumerable.Range(1, 600).Select(i => TestSupport.Rec(i, $"Item {i}", cats[rng.Next(cats.Length)], rng.Next(3) == 0 ? "Sub" + rng.Next(4) : null,
            tags: [$"tag{rng.Next(20)}"], materials: [mats[rng.Next(mats.Length)], mats[rng.Next(mats.Length)]], colors: [rng.Next(2) == 0 ? "branco" : "preto"],
            envs: [envs[rng.Next(envs.Length)]], folder: "F" + rng.Next(8), version: (2018 + rng.Next(8)).ToString(), preview: rng.Next(5) != 0)).ToList();
        var index = new LibraryIndex(records);
        for (var round = 0; round < 40; round++)
        {
            var spec = new FilterSpec { Text = rng.Next(3) == 0 ? "item" : "", OnlyWithPreview = rng.Next(4) == 0 };
            if (rng.Next(2) == 0) spec.Environments.Add(envs[rng.Next(envs.Length)]);
            if (rng.Next(2) == 0) spec.Materials.Add(mats[rng.Next(mats.Length)]);
            if (rng.Next(3) == 0) spec.Colors.Add("branco");
            if (rng.Next(3) == 0) spec.Folders.Add("f" + rng.Next(8));
            if (rng.Next(3) == 0) spec.Categories.Add(index.All[rng.Next(index.All.Count)].CategoryKey);
            if (rng.Next(4) == 0) spec.RevitVersions.Add((2018 + rng.Next(8)).ToString());
            AssertSame(index, spec);
        }
    }
}
