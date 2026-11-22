using System.Runtime.CompilerServices;
using Gestaobim.Familias.Core;

namespace GBFamilias.Core.Tests;

internal static class TestSupport
{
    [ModuleInitializer]
    internal static void Init() => SQLitePCL.Batteries_V2.Init();

    public static FamilyRecord Rec(long id, string name, string? category = null, string? sub = null,
        string[]? tags = null, string[]? materials = null, string[]? colors = null, string[]? envs = null,
        string? style = null, string? folder = null, string? version = null, bool preview = true,
        string? description = null, string? friendly = null) => new()
    {
        Id = id,
        Sha = $"sha{id}",
        Name = name,
        RelPath = $"{folder}\\{name}.rfa",
        FolderCategory = folder,
        FriendlyName = friendly,
        Description = description,
        Category = category,
        Subcategory = sub,
        Tags = tags ?? [],
        Materials = materials ?? [],
        Colors = colors ?? [],
        Environments = envs ?? [],
        Style = style,
        RevitVersion = version,
        HasPreview = preview,
        Enriched = category is not null,
    };

    /// <summary>Uma biblioteca pequena e variada para os testes de filtro.</summary>
    public static LibraryIndex SampleIndex() => new(
    [
        Rec(1, "Sofa 56", "Mobiliário", "Sofás", ["sofa", "estofado", "três lugares"], ["tecido"], ["cinza"], ["sala"], "contemporâneo", "SALA", "2020", friendly: "Sofá de três lugares"),
        Rec(2, "Sofa 55", "Mobiliário", "Sofás", ["sofa", "canto"], ["couro"], ["preto"], ["sala"], null, "SALA", "2012"),
        Rec(3, "Mesa 10", "Mobiliário", "Mesas", ["mesa", "jantar"], ["madeira"], ["marrom"], ["sala", "cozinha"], "rústico", "SALA", "2020"),
        Rec(4, "Pedestal Sink", "Hidráulica", "Pias", ["pia", "lavatório", "banheiro"], ["porcelana"], ["branco"], ["banheiro"], null, "WC", "2018", description: "Lavatório de pedestal."),
        Rec(5, "Vaso de Planta 13", "Decoração", "Vasos", ["vaso", "planta"], ["cerâmica"], ["verde"], ["sala"], null, "DECOR", "~2008", preview: false),
        Rec(6, "Carro 1", "Veículo", null, ["carro"], [], ["vermelho"], [], null, "VEICULOS", "2020"),
        Rec(7, "Carro 2", "Veículos", null, ["carro"], [], ["azul"], [], null, "VEICULOS", "2020"),
        Rec(8, "Sem IA", null, null, [], [], [], [], null, "SALA", null),
    ]);
}
