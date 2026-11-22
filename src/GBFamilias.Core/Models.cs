namespace Gestaobim.Familias.Core;

/// <summary>Uma familia da biblioteca, com os dados do banco e os gerados pela IA.</summary>
public sealed class FamilyRecord
{
    /// <summary>Tamanho do .rfa em bytes (para saber se ja foi baixado sem calcular o hash).</summary>
    public long SizeBytes { get; init; }
    public long Id { get; init; }
    /// <summary>Hash do conteudo: chave estavel para favoritos mesmo se o banco for reconstruido.</summary>
    public string Sha { get; init; } = "";
    public string Name { get; init; } = "";
    public string RelPath { get; init; } = "";
    public string? FolderCategory { get; init; }
    public string? FolderHint { get; init; }
    public string? FriendlyName { get; init; }
    public string? Description { get; init; }
    public string? Category { get; set; }
    public string CategoryKey { get; set; } = "";
    public string? Subcategory { get; set; }
    public string SubcategoryKey { get; set; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Materials { get; init; } = [];
    public IReadOnlyList<string> Colors { get; init; } = [];
    public IReadOnlyList<string> Environments { get; init; } = [];
    public string? Style { get; init; }
    /// <summary>Ano do Revit em que a familia foi salva ("2020"), ou "~2008" quando so se estimou pelo build.</summary>
    public string? RevitVersion { get; init; }
    public string? RevitCategory { get; init; }
    public bool HasPreview { get; init; }
    /// <summary>Quantas copias identicas existem na pasta de origem alem desta.</summary>
    public int Copies { get; init; }
    public bool Enriched { get; init; }

    public string Title => string.IsNullOrWhiteSpace(FriendlyName) ? Name : FriendlyName!;

    // Pre-calculados na carga para a busca ser instantanea (sem acento e em minuscula).
    internal string FoldedName = "";
    internal string FoldedBlob = "";
    internal string FoldedCategory = "";
    internal string FoldedSub = "";
    internal string FoldedFolder = "";
    internal string FoldedStyle = "";
    internal string[] FoldedTags = [];
    internal string[] FoldedMaterials = [];
    internal string[] FoldedColors = [];
    internal string[] FoldedEnvironments = [];

    /// <summary>Chamar depois de CategoryNormalizer.Apply.</summary>
    internal void Prepare()
    {
        FoldedName = TextNorm.Fold(Title + " " + Name);
        FoldedCategory = TextNorm.Fold(Category);
        FoldedSub = TextNorm.Fold(Subcategory);
        FoldedFolder = TextNorm.Fold((FolderCategory ?? "") + " " + (FolderHint ?? ""));
        FoldedStyle = TextNorm.Fold(Style);
        FoldedTags = Tags.Select(TextNorm.Fold).Where(t => t.Length > 0).ToArray();
        FoldedMaterials = Materials.Select(TextNorm.Fold).Where(t => t.Length > 0).ToArray();
        FoldedColors = Colors.Select(TextNorm.Fold).Where(t => t.Length > 0).ToArray();
        FoldedEnvironments = Environments.Select(TextNorm.Fold).Where(t => t.Length > 0).ToArray();
        FoldedBlob = string.Join(' ', FoldedName, FoldedCategory, FoldedSub, FoldedFolder, FoldedStyle,
            string.Join(' ', FoldedTags), string.Join(' ', FoldedMaterials), string.Join(' ', FoldedColors),
            string.Join(' ', FoldedEnvironments), TextNorm.Fold(Description), TextNorm.Fold(RevitCategory));
    }
}

public enum Facet
{
    Category,
    Folder,
    Environment,
    Material,
    Color,
    Style,
    RevitVersion,
    RevitCategory,
}

public sealed record FacetCount(string Key, string Label, int Count);

public enum SortMode
{
    Relevance,
    Name,
    Category,
}

/// <summary>Filtros ativos. Dentro de um grupo vale "ou"; entre grupos vale "e".</summary>
public sealed class FilterSpec
{
    public string Text { get; set; } = "";
    public HashSet<string> Categories { get; } = [];
    /// <summary>Chaves "categoria|subcategoria".</summary>
    public HashSet<string> Subcategories { get; } = [];
    public HashSet<string> Folders { get; } = [];
    public HashSet<string> Environments { get; } = [];
    public HashSet<string> Materials { get; } = [];
    public HashSet<string> Colors { get; } = [];
    public HashSet<string> Styles { get; } = [];
    public HashSet<string> RevitVersions { get; } = [];
    public HashSet<string> RevitCategories { get; } = [];
    public bool OnlyWithPreview { get; set; }
    /// <summary>Quando definido, so familias com estes hashes (favoritos, colecao, recentes).</summary>
    public HashSet<string>? OnlyShas { get; set; }
    /// <summary>Filtro extra da interface (por exemplo "somente baixadas"). Vale nos resultados e nas contagens.</summary>
    public Func<FamilyRecord, bool>? Extra { get; set; }

    public HashSet<string> Selection(Facet facet) => facet switch
    {
        Facet.Category => Categories,
        Facet.Folder => Folders,
        Facet.Environment => Environments,
        Facet.Material => Materials,
        Facet.Color => Colors,
        Facet.Style => Styles,
        Facet.RevitVersion => RevitVersions,
        Facet.RevitCategory => RevitCategories,
        _ => throw new ArgumentOutOfRangeException(nameof(facet)),
    };

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text) && !OnlyWithPreview && OnlyShas is null && Extra is null
        && Categories.Count + Subcategories.Count + Folders.Count + Environments.Count + Materials.Count
           + Colors.Count + Styles.Count + RevitVersions.Count + RevitCategories.Count == 0;

    public void Clear()
    {
        Text = "";
        Categories.Clear(); Subcategories.Clear(); Folders.Clear(); Environments.Clear(); Materials.Clear();
        Colors.Clear(); Styles.Clear(); RevitVersions.Clear(); RevitCategories.Clear();
        OnlyWithPreview = false;
        OnlyShas = null;
        Extra = null;
    }

    public FilterSpec Clone()
    {
        var copy = new FilterSpec { Text = Text, OnlyWithPreview = OnlyWithPreview, Extra = Extra, OnlyShas = OnlyShas is null ? null : [.. OnlyShas] };
        foreach (var f in Enum.GetValues<Facet>()) copy.Selection(f).UnionWith(Selection(f));
        copy.Subcategories.UnionWith(Subcategories);
        return copy;
    }
}
