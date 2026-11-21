using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>What the editor may change for a given appearance schema, and how it labels it.</summary>
public sealed record SchemaCapabilities(
    bool Color, bool Roughness, bool Reflectivity, bool Glossiness, bool Transparency, bool Texture,
    string ColorLabel, string PropsLabel, string TextureLabel)
{
    public static SchemaCapabilities For(string schema, bool hasTexture)
    {
        var caps = new SchemaCapabilities(true, false, false, false, false, hasTexture,
            "Cor Difusa (Render)", "Propriedades (Render)", "Textura (Preview)");
        caps = schema switch
        {
            "GenericSchema" => caps with { Roughness = true, Reflectivity = true, Glossiness = true, Transparency = true },
            "CeramicSchema" => caps with { ColorLabel = "Cor/Textura Ceramica", PropsLabel = "Propriedades Ceramicas", Texture = true },
            "MetalSchema" => caps with { Roughness = true, ColorLabel = "Cor do Metal", PropsLabel = "Propriedades do Metal" },
            "GlassSchema" => caps with { Transparency = true, ColorLabel = "Cor do Vidro", PropsLabel = "Propriedades do Vidro" },
            _ => caps,
        };
        return hasTexture ? caps with { Color = false } : caps;
    }
}

public static class AppearanceSchemas
{
    public static readonly IReadOnlyDictionary<string, string[]> ExtraProps = new Dictionary<string, string[]>
    {
        ["GenericSchema"] =
        [
            "surfacealbedo", "surfaceanisotropy", "surfacecutout", "surfacendf_type", "surfacerotation",
            "surfaceroughness", "transparentcolor", "transparentdistance", "transparentior", "interiormodel", "PatternOffset",
        ],
        ["CeramicSchema"] = ["PatternOffset", "interiormodel"],
        ["MetalSchema"] =
        [
            "surfacealbedo", "surfaceanisotropy", "surfacendf_type", "surfacerotation", "surfaceroughness", "interiormodel",
        ],
        ["GlassSchema"] = ["transparentcolor", "transparentdistance", "transparentior", "interiormodel"],
    };

    public static readonly IReadOnlyDictionary<string, string> DisplayNames = new Dictionary<string, string>
    {
        ["ceramic_pattern_amount"] = "Quantidade Padrao",
        ["ceramic_pattern"] = "Padrao",
        ["ceramic_bump_amount"] = "Intensidade Bump",
        ["ceramic_bump"] = "Bump",
        ["ceramic_ao_on"] = "Occlusao Ambiental",
        ["ceramic_ao_distance"] = "Distancia AO",
        ["ceramic_ao_samples"] = "Amostras AO",
        ["ceramic_ao_details"] = "Detalhes AO",
        ["ceramic_roundcorners_on"] = "Cantos Arredondados",
        ["ceramic_roundcorners_radius"] = "Raio Cantos",
        ["ceramic_roundcorners_allow_different_materials"] = "Materiais Diferentes nos Cantos",
        ["ceramic_application"] = "Aplicacao",
        ["ceramic_type"] = "Tipo",
        ["ceramic_color_by_object"] = "Cor por Objeto",
        ["ceramic_color_colorspace"] = "Espaco de Cor",
        ["ceramic_refl_depth"] = "Profundidade Reflexo",
        ["reflection_glossy_samples"] = "Amostras Reflexo",
        ["generic_diffuse"] = "Cor Difusa",
        ["generic_roughness"] = "Rugosidade",
        ["generic_reflectivity"] = "Refletividade",
        ["generic_glossiness"] = "Brilho",
        ["generic_transparency"] = "Transparencia",
        ["generic_bump"] = "Bump",
        ["generic_cutout"] = "Recorte",
        ["PatternOffset"] = "Offset Padrao",
        ["interiormodel"] = "Modelo Interior",
        ["surfacealbedo"] = "Albedo da Superficie",
        ["surfaceanisotropy"] = "Anisotropia da Superficie",
        ["surfacecutout"] = "Recorte da Superficie",
        ["surfacendf_type"] = "Tipo NDF da Superficie",
        ["surfacerotation"] = "Rotacao da Superficie",
        ["surfaceroughness"] = "Rugosidade da Superficie",
        ["transparentcolor"] = "Cor da Transparencia",
        ["transparentdistance"] = "Distancia da Transparencia",
        ["transparentior"] = "IOR da Transparencia",
        ["texture_main"] = "Textura Principal",
        ["texture_bump"] = "Textura Bump",
        ["texture_cutout"] = "Textura de Recorte",
        ["texture_transparency"] = "Textura de Transparencia",
    };

    /// <summary>Properties that are shown elsewhere in the editor or are internal bookkeeping.</summary>
    public static readonly IReadOnlySet<string> Skipped = new HashSet<string>
    {
        "BaseSchema", "SchemaVersion", "version", "revision", "assettype", "localtype", "keyword", "category",
        "localname", "UIName", "Hidden", "mode", "common_Shared_Asset", "common_Tint_toggle", "common_Tint_color",
        "common_Tint_color_colorspace", "ImplementationPreview", "ImplementationOGS", "ImplementationGeneric",
        "ImplementationMentalRay", "AdvancedUIDefinition", "UIDefinition", "AssetLibID", "VersionGUID", "ExchangeGUID",
        "thumbnail", "swatch", "description", "generic_diffuse", "generic_roughness", "generic_reflectivity",
        "generic_glossiness", "generic_transparency", "ceramic_color", "ceramic_bump_amount", "ceramic_pattern_amount",
        "ceramic_ao_on", "ceramic_ao_distance", "ceramic_ao_samples", "ceramic_ao_details", "ceramic_roundcorners_on",
        "ceramic_roundcorners_radius", "ceramic_roundcorners_allow_different_materials", "ceramic_application",
        "ceramic_type", "ceramic_color_by_object", "ceramic_color_colorspace", "ceramic_refl_depth",
        "reflection_glossy_samples",
    };

    public static string Display(string key) => DisplayNames.TryGetValue(key, out var shown) ? shown : key;
}

/// <summary>Typed access to the loosely typed Revit asset properties.</summary>
public static class AssetProps
{
    public static IEnumerable<AssetProperty> All(Asset asset)
    {
        for (var i = 0; i < asset.Size; i++)
            yield return asset.Get(i);
    }

    public static Asset? Connected(AssetProperty property)
    {
        try
        {
            return property.NumberOfConnectedProperties > 0 ? property.GetSingleConnectedAsset() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? BaseSchema(Asset asset) => (asset.FindByName("BaseSchema") as AssetPropertyString)?.Value;

    public static bool TryGetColor(AssetProperty? property, out Color color)
    {
        color = null!;
        if (property is not AssetPropertyDoubleArray4d array) return false;
        try
        {
            color = array.GetValueAsColor();
            return color is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryGetDouble(AssetProperty? property, out double value)
    {
        switch (property)
        {
            case AssetPropertyDouble d: value = d.Value; return true;
            case AssetPropertyFloat f: value = f.Value; return true;
            case AssetPropertyDistance dist: value = dist.Value; return true;
            case AssetPropertyInteger i: value = i.Value; return true;
            default: value = 0; return false;
        }
    }

    public static string? TryGetString(AssetProperty? property) => property switch
    {
        AssetPropertyString s => s.Value,
        AssetPropertyReference => null,
        _ => null,
    };

    /// <summary>Sets a property to a value of the type it holds. Returns false when the types do not match.</summary>
    public static bool TrySet(AssetProperty property, object value)
    {
        try
        {
            switch (property)
            {
                case AssetPropertyString s when value is string text: s.Value = text; return true;
                case AssetPropertyDouble d: d.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true;
                case AssetPropertyDistance dist: dist.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true;
                case AssetPropertyFloat f: f.Value = Convert.ToSingle(value, CultureInfo.InvariantCulture); return true;
                case AssetPropertyBoolean b when value is bool flag: b.Value = flag; return true;
                case AssetPropertyInteger i: i.Value = Convert.ToInt32(value, CultureInfo.InvariantCulture); return true;
                default: return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>How a property reads in the editor's "extra properties" list, or null when it has nothing worth showing.</summary>
    public static string? Format(AssetProperty property)
    {
        try
        {
            switch (property)
            {
                case AssetPropertyDoubleArray4d array:
                    if (TryGetColor(array, out var color))
                        return $"RGB({color.Red}, {color.Green}, {color.Blue})";
                    return FormatDoubles(array.GetValueAsDoubles());
                case AssetPropertyDoubleArray3d array3:
                    return FormatDoubles(array3.GetValueAsDoubles());
                case AssetPropertyDoubleArray2d array2:
                    return FormatDoubles(ToList(array2.Value));
                case AssetPropertyDouble d: return d.Value.ToString("R", CultureInfo.InvariantCulture);
                case AssetPropertyDistance dist: return dist.Value.ToString("R", CultureInfo.InvariantCulture);
                case AssetPropertyFloat f: return f.Value.ToString("R", CultureInfo.InvariantCulture);
                case AssetPropertyInteger i: return i.Value.ToString(CultureInfo.InvariantCulture);
                case AssetPropertyBoolean b: return b.Value ? "True" : "False";
                case AssetPropertyString s: return string.IsNullOrWhiteSpace(s.Value) ? null : s.Value.Trim();
                default: return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<double> ToList(DoubleArray? array)
    {
        var list = new List<double>();
        if (array is null) return list;
        for (var i = 0; i < array.Size; i++)
            list.Add(array.get_Item(i));
        return list;
    }

    private static string? FormatDoubles(IEnumerable<double>? values)
    {
        var list = values?.ToList();
        return list is { Count: > 0 } ? string.Join(", ", list.Select(v => v.ToString("0.000", CultureInfo.InvariantCulture))) : null;
    }
}
