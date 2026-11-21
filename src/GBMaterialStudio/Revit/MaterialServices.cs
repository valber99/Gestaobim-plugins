using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Gestaobim.MaterialStudio.UI;

namespace Gestaobim.MaterialStudio.Revit;

public enum TextureRole { Main, Bump, Cutout, Transparency }

/// <summary>A fill pattern as the editor's combo boxes show it.</summary>
public sealed class PatternItem(string name, ElementId id, System.Windows.Media.Brush? previewBrush, bool drafting)
{
    public string Name { get; } = name;
    public ElementId Id { get; } = id;
    public System.Windows.Media.Brush? PreviewBrush { get; } = previewBrush;
    public bool Drafting { get; } = drafting;

    public static PatternItem None() => new("<Nenhum>", ElementId.InvalidElementId, Visuals.ToBrush("#FFFFFF"), true);

    public bool IsSolidFill => Name is "<Solid Fill>" or "<Preenchimento solido>";
}

public sealed record PropItem(string Key, string Value);

public sealed class TextureSlotData
{
    public string Path { get; set; } = "";
    public double Brightness { get; set; } = 1.0;
    public bool Invert { get; set; }
    public bool LinkTransforms { get; set; }
    public double OffsetXCm { get; set; }
    public double OffsetYCm { get; set; }
    public double Rotation { get; set; }
    public double SizeXCm { get; set; }
    public double SizeYCm { get; set; }
    public bool ScaleLock { get; set; }
    public bool RepeatU { get; set; } = true;
    public bool RepeatV { get; set; } = true;
}

public sealed class AppearanceData
{
    public double DiffuseR { get; set; } = 0.93;
    public double DiffuseG { get; set; } = 0.93;
    public double DiffuseB { get; set; } = 0.93;
    public double Roughness { get; set; } = 0.5;
    public double Reflectivity { get; set; }
    public double Glossiness { get; set; }
    public double Transparency { get; set; }
    public string Schema { get; set; } = "";
    public Dictionary<TextureRole, string> Textures { get; set; } = EmptyTextures();
    public string MainTexturePath => Textures.GetValueOrDefault(TextureRole.Main) ?? "";
    public bool HasTexture => MainTexturePath.Length > 0;

    public static Dictionary<TextureRole, string> EmptyTextures() => new()
    {
        [TextureRole.Main] = "", [TextureRole.Bump] = "", [TextureRole.Cutout] = "", [TextureRole.Transparency] = "",
    };
}

/// <summary>Everything the graphics tab edits, in the units Revit stores.</summary>
public sealed class MaterialGraphics
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public int Transparency { get; set; }
    public int Shininess { get; set; }
    public int Smoothness { get; set; }
    public bool UseRenderAppearance { get; set; }
    public ElementId? SurfaceForegroundPatternId { get; set; }
    public ElementId? SurfaceBackgroundPatternId { get; set; }
    public ElementId? CutForegroundPatternId { get; set; }
    public ElementId? CutBackgroundPatternId { get; set; }
    public Color? SurfaceForegroundColor { get; set; }
    public Color? SurfaceBackgroundColor { get; set; }
    public Color? CutForegroundColor { get; set; }
    public Color? CutBackgroundColor { get; set; }
}

public sealed class MaterialIdentity
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Url { get; set; } = "";
    public string MaterialClass { get; set; } = "";
    public string Category { get; set; } = "";
    public string Mark { get; set; } = "";
    public string Comments { get; set; } = "";
}

/// <summary>Accepts only faces that carry a material.</summary>
public sealed class MaterialFaceFilter(Document doc) : ISelectionFilter
{
    public bool AllowElement(Element elem) => true;

    public bool AllowReference(Reference reference, XYZ position)
    {
        try
        {
            var element = doc.GetElement(reference);
            return element?.GetGeometryObjectFromReference(reference) is Face face && ElementIds.IsValid(face.MaterialElementId);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Accepts any face (painting a material onto it).</summary>
public sealed class PaintFaceFilter(Document doc) : ISelectionFilter
{
    public bool AllowElement(Element elem) => true;

    public bool AllowReference(Reference reference, XYZ position)
    {
        try
        {
            return doc.GetElement(reference)?.GetGeometryObjectFromReference(reference) is Face;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public static class MaterialServices
{
    private static readonly string[] BitmapRoles = ["main", "bump", "cutout", "transparency"];

    // ---------------------------------------------------------------- lookups

    public static List<PatternItem> GetFillPatterns(Document doc)
    {
        var patterns = new List<PatternItem>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
        {
            var pattern = element.GetFillPattern();
            if (pattern is null) continue;
            var name = pattern.IsSolidFill ? "<Solid Fill>" : pattern.Name;
            patterns.Add(new PatternItem(name, element.Id, Visuals.PatternPreview(element), pattern.Target == FillPatternTarget.Drafting));
        }
        return patterns.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Material? GetMaterialFromFace(Document doc, Reference reference)
    {
        var element = doc.GetElement(reference);
        if (element is null) return null;
        if (element.GetGeometryObjectFromReference(reference) is Face face && ElementIds.IsValid(face.MaterialElementId))
            return doc.GetElement(face.MaterialElementId) as Material;
        if (element is FamilyInstance instance)
        {
            var parameter = instance.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (parameter is not null && ElementIds.IsValid(parameter.AsElementId()))
                return doc.GetElement(parameter.AsElementId()) as Material;
        }
        return null;
    }

    private static Asset? RenderingAsset(Document doc, Material material)
    {
        if (!ElementIds.IsValid(material.AppearanceAssetId)) return null;
        return (doc.GetElement(material.AppearanceAssetId) as AppearanceAssetElement)?.GetRenderingAsset();
    }

    // ---------------------------------------------------------------- appearance (read)

    /// <summary>The schema name and the extra properties worth showing for a material's appearance.</summary>
    public static (string Schema, List<PropItem> Props) ReadAppearanceAsset(Document doc, Material material)
    {
        var props = new List<PropItem>();
        var asset = RenderingAsset(doc, material);
        if (asset is null) return ("", props);

        var schema = AssetProps.BaseSchema(asset) ?? "";
        AppearanceSchemas.ExtraProps.TryGetValue(schema, out var allowed);
        var textures = CollectAssetTextures(asset);

        try
        {
            foreach (var property in AssetProps.All(asset))
            {
                var name = property.Name;
                if (AppearanceSchemas.Skipped.Contains(name)) continue;
                if (allowed is { Length: > 0 } && !allowed.Contains(name)) continue;
                var shown = AssetProps.Format(property);
                if (shown is not null)
                    props.Add(new PropItem(AppearanceSchemas.Display(name), shown));
            }
        }
        catch (Exception)
        {
            // A schema that cannot be enumerated still shows the textures below.
        }

        foreach (var (role, key) in new[] { (TextureRole.Main, "main"), (TextureRole.Bump, "bump"), (TextureRole.Cutout, "cutout"), (TextureRole.Transparency, "transparency") })
        {
            if (textures.GetValueOrDefault(role) is { Length: > 0 } path)
                props.Add(new PropItem(AppearanceSchemas.Display("texture_" + key), path));
        }
        return (schema, props);
    }

    public static AppearanceData ReadAppearanceEditable(Document doc, Material material)
    {
        var data = new AppearanceData();
        var asset = RenderingAsset(doc, material);
        if (asset is null) return data;

        data.Schema = AssetProps.BaseSchema(asset) ?? "";
        data.Textures = CollectAssetTextures(asset);

        foreach (var name in new[] { "generic_diffuse", "ceramic_color", "metal_color", "glass_color" })
        {
            var property = asset.FindByName(name);
            if (property is null) continue;
            if (AssetProps.TryGetColor(property, out var color))
            {
                data.DiffuseR = color.Red / 255.0;
                data.DiffuseG = color.Green / 255.0;
                data.DiffuseB = color.Blue / 255.0;
            }
            else if (property is AssetPropertyDoubleArray4d array)
            {
                var doubles = array.GetValueAsDoubles();
                if (doubles is { Count: >= 3 })
                {
                    data.DiffuseR = doubles[0];
                    data.DiffuseG = doubles[1];
                    data.DiffuseB = doubles[2];
                }
            }
            break;
        }

        // Later entries win, as before: metal roughness overrides generic roughness.
        foreach (var name in new[] { "generic_roughness", "metal_roughness" })
            if (AssetProps.TryGetDouble(asset.FindByName(name), out var value)) data.Roughness = value;
        if (AssetProps.TryGetDouble(asset.FindByName("generic_reflectivity"), out var reflectivity)) data.Reflectivity = reflectivity;
        if (AssetProps.TryGetDouble(asset.FindByName("generic_glossiness"), out var glossiness)) data.Glossiness = glossiness;
        if (AssetProps.TryGetDouble(asset.FindByName("generic_transparency"), out var transparency)) data.Transparency = transparency;
        if (AssetProps.TryGetDouble(asset.FindByName("surfaceroughness"), out var surfaceRoughness)) data.Roughness = surfaceRoughness;
        return data;
    }

    // ---------------------------------------------------------------- textures (read)

    private static string InferRole(string? hint)
    {
        hint = (hint ?? "").ToLowerInvariant();
        string[] main = ["diffuse", "albedo", "color", "surfacealbedo", "ceramic_color", "metal_color", "glass_color"];
        if (main.Any(hint.Contains)) return "main";
        if (hint.Contains("bump") || hint.Contains("normal")) return "bump";
        if (hint.Contains("cutout") || hint.Contains("opacity")) return "cutout";
        if (hint.Contains("transparen")) return "transparency";
        return "other";
    }

    private static TextureRole ToRole(string role) => role switch
    {
        "bump" => TextureRole.Bump,
        "cutout" => TextureRole.Cutout,
        "transparency" => TextureRole.Transparency,
        _ => TextureRole.Main,
    };

    private static string RoleName(TextureRole role) => role switch
    {
        TextureRole.Bump => "bump",
        TextureRole.Cutout => "cutout",
        TextureRole.Transparency => "transparency",
        _ => "main",
    };

    public static Dictionary<TextureRole, string> CollectAssetTextures(Asset asset)
    {
        var found = AppearanceData.EmptyTextures();
        CollectTextures(asset, null, found, 0);
        return found;
    }

    private static void CollectTextures(Asset? asset, string? roleHint, Dictionary<TextureRole, string> found, int depth)
    {
        if (asset is null || depth > 6) return;
        try
        {
            foreach (var property in AssetProps.All(asset))
            {
                var role = roleHint ?? InferRole(property.Name);
                if (property.Name == UnifiedBitmap.UnifiedbitmapBitmap)
                {
                    var path = AssetProps.TryGetString(property);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var key = BitmapRoles.Contains(role) ? ToRole(role) : TextureRole.Main;
                        if (string.IsNullOrEmpty(found[key])) found[key] = path;
                    }
                }
                var connected = AssetProps.Connected(property);
                if (connected is not null)
                    CollectTextures(connected, InferRole(property.Name), found, depth + 1);
            }
        }
        catch (Exception)
        {
            // An unreadable branch does not stop the others.
        }
    }

    private static bool RoleMatches(string target, string role) =>
        target == role || (target == "main" && role is "main" or "other");

    private static Asset? FindBitmapAsset(Asset? asset, string target, int depth, string? roleHint = null)
    {
        if (asset is null || depth > 6) return null;
        try
        {
            foreach (var property in AssetProps.All(asset))
            {
                var role = roleHint ?? InferRole(property.Name);
                var connected = AssetProps.Connected(property);
                if (connected is null) continue;
                if (connected.Name == "UnifiedBitmapSchema" && RoleMatches(target, role))
                    return connected;
                var nested = FindBitmapAsset(connected, target, depth + 1, InferRole(property.Name));
                if (nested is not null) return nested;
            }
        }
        catch (Exception)
        {
            // Fall through: not found.
        }
        return null;
    }

    public static Dictionary<TextureRole, bool> GetTextureSlotAvailability(Document doc, Material material)
    {
        var slots = Enum.GetValues<TextureRole>().ToDictionary(r => r, _ => false);
        var asset = RenderingAsset(doc, material);
        if (asset is null) return slots;
        foreach (var role in slots.Keys.ToList())
            slots[role] = FindBitmapAsset(asset, RoleName(role), 0) is not null;
        return slots;
    }

    private static double ToCm(double internalUnits) =>
        UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Centimeters);

    private static double FromCm(double centimeters) =>
        UnitUtils.ConvertToInternalUnits(centimeters, UnitTypeId.Centimeters);

    public static TextureSlotData ReadTextureSlotData(Document doc, Material material, TextureRole role)
    {
        var result = new TextureSlotData();
        var bitmap = FindBitmapAsset(RenderingAsset(doc, material), RoleName(role), 0);
        if (bitmap is null) return result;

        AssetProperty? P(string name) => bitmap.FindByName(name);
        if (AssetProps.TryGetString(P(UnifiedBitmap.UnifiedbitmapBitmap)) is { } path) result.Path = path;
        if (AssetProps.TryGetDouble(P(UnifiedBitmap.UnifiedbitmapRGBAmount), out var brightness)) result.Brightness = brightness;
        if (P(UnifiedBitmap.UnifiedbitmapInvert) is AssetPropertyBoolean invert) result.Invert = invert.Value;
        if (P(UnifiedBitmap.TextureLinkTextureTransforms) is AssetPropertyBoolean link) result.LinkTransforms = link.Value;
        if (AssetProps.TryGetDouble(P(UnifiedBitmap.TextureRealWorldOffsetX), out var offsetX)) result.OffsetXCm = ToCm(offsetX);
        if (AssetProps.TryGetDouble(P(UnifiedBitmap.TextureRealWorldOffsetY), out var offsetY)) result.OffsetYCm = ToCm(offsetY);
        if (AssetProps.TryGetDouble(P(UnifiedBitmap.TextureWAngle), out var angle)) result.Rotation = angle;
        if (AssetProps.TryGetDouble(P(UnifiedBitmap.TextureRealWorldScaleX), out var scaleX)) result.SizeXCm = ToCm(scaleX);
        if (AssetProps.TryGetDouble(P(UnifiedBitmap.TextureRealWorldScaleY), out var scaleY)) result.SizeYCm = ToCm(scaleY);
        if (P(UnifiedBitmap.TextureScaleLock) is AssetPropertyBoolean lockScale) result.ScaleLock = lockScale.Value;
        if (P(UnifiedBitmap.TextureURepeat) is AssetPropertyBoolean repeatU) result.RepeatU = repeatU.Value;
        if (P(UnifiedBitmap.TextureVRepeat) is AssetPropertyBoolean repeatV) result.RepeatV = repeatV.Value;
        return result;
    }

    // ---------------------------------------------------------------- appearance and texture (write)

    private static void Abort(AppearanceAssetEditScope scope, Transaction transaction, string step, Exception error)
    {
        Failures.Record(step, error);
        try { scope.Dispose(); } catch (Exception) { }
        try { transaction.RollBack(); } catch (Exception) { }
    }

    /// <summary>Writes every texture setting into the slot's bitmap. One undo entry.</summary>
    public static bool ApplyTextureSlotData(Document doc, Material material, TextureRole role, TextureSlotData data)
    {
        if (!ElementIds.IsValid(material.AppearanceAssetId)) return false;
        using var transaction = new Transaction(doc, "Editar Textura");
        transaction.Start();
        var scope = new AppearanceAssetEditScope(doc);
        try
        {
            var editable = scope.Start(material.AppearanceAssetId);
            var bitmap = FindBitmapAsset(editable, RoleName(role), 0);
            if (bitmap is null)
            {
                scope.Commit(false);
                transaction.RollBack();
                Failures.Record("textura", "slot sem textura conectada");
                return false;
            }

            (string Name, object Value)[] values =
            [
                (UnifiedBitmap.UnifiedbitmapBitmap, data.Path),
                (UnifiedBitmap.UnifiedbitmapRGBAmount, data.Brightness),
                (UnifiedBitmap.UnifiedbitmapInvert, data.Invert),
                (UnifiedBitmap.TextureLinkTextureTransforms, data.LinkTransforms),
                (UnifiedBitmap.TextureRealWorldOffsetX, FromCm(data.OffsetXCm)),
                (UnifiedBitmap.TextureRealWorldOffsetY, FromCm(data.OffsetYCm)),
                (UnifiedBitmap.TextureWAngle, data.Rotation),
                (UnifiedBitmap.TextureRealWorldScaleX, FromCm(Math.Max(0.01, data.SizeXCm))),
                (UnifiedBitmap.TextureRealWorldScaleY, FromCm(Math.Max(0.01, data.SizeYCm))),
                (UnifiedBitmap.TextureScaleLock, data.ScaleLock),
                (UnifiedBitmap.TextureURepeat, data.RepeatU),
                (UnifiedBitmap.TextureVRepeat, data.RepeatV),
            ];
            foreach (var (name, value) in values)
            {
                var property = bitmap.FindByName(name);
                if (property is not null)
                    AssetProps.TrySet(property, value);
            }
            scope.Commit(true);
            transaction.Commit();
            return true;
        }
        catch (Exception error)
        {
            Abort(scope, transaction, "textura", error);
            return false;
        }
    }

    /// <summary>Points the slot's bitmap at another image file.</summary>
    public static bool SetTextureSlot(Document doc, Material material, TextureRole role, string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return false;
        if (!ElementIds.IsValid(material.AppearanceAssetId)) return false;
        using var transaction = new Transaction(doc, "Alterar Textura");
        transaction.Start();
        var scope = new AppearanceAssetEditScope(doc);
        try
        {
            var editable = scope.Start(material.AppearanceAssetId);
            var bitmap = FindBitmapAsset(editable, RoleName(role), 0);
            var property = bitmap?.FindByName(UnifiedBitmap.UnifiedbitmapBitmap);
            if (property is not null && AssetProps.TrySet(property, imagePath))
            {
                scope.Commit(true);
                transaction.Commit();
                return true;
            }
            scope.Commit(false);
            transaction.RollBack();
            Failures.Record("textura", "nenhum slot compativel encontrado");
            return false;
        }
        catch (Exception error)
        {
            Abort(scope, transaction, "textura", error);
            return false;
        }
    }

    /// <summary>Writes the render appearance. A material with no appearance asset has nothing to write and counts as done.</summary>
    public static bool ApplyAppearanceChanges(Document doc, ElementId assetId, double r, double g, double b,
        double roughness, double reflectivity, double glossiness, double transparency)
    {
        if (!ElementIds.IsValid(assetId)) return true;
        var element = doc.GetElement(assetId) as AppearanceAssetElement;
        var asset = element?.GetRenderingAsset();
        if (asset is null) return true;

        var schema = AssetProps.BaseSchema(asset) ?? "";
        string color, rough = "", reflect = "", gloss = "", transp = "";
        switch (schema)
        {
            case "CeramicSchema": color = "ceramic_color"; break;
            case "MetalSchema": color = "metal_color"; rough = "metal_roughness"; break;
            case "GlassSchema": color = "glass_color"; transp = "glass_transmittance"; break;
            default:
                color = "generic_diffuse"; rough = "generic_roughness"; reflect = "generic_reflectivity";
                gloss = "generic_glossiness"; transp = "generic_transparency";
                break;
        }

        using var transaction = new Transaction(doc, "Alterar Appearance");
        transaction.Start();
        var scope = new AppearanceAssetEditScope(doc);
        try
        {
            var editable = scope.Start(assetId);
            if (editable.FindByName(color) is AssetPropertyDoubleArray4d colorProperty)
            {
                try
                {
                    colorProperty.SetValueAsColor(new Color(Convert.ToByte(r * 255), Convert.ToByte(g * 255), Convert.ToByte(b * 255)));
                }
                catch (Exception)
                {
                    try { colorProperty.SetValueAsDoubles([r, g, b, 1.0]); }
                    catch (Exception) { }
                }
            }
            foreach (var (name, value) in new[] { (rough, roughness), (reflect, reflectivity), (gloss, glossiness), (transp, transparency) })
            {
                if (name.Length == 0) continue;
                var property = editable.FindByName(name);
                if (property is not null)
                    AssetProps.TrySet(property, value);
            }
            scope.Commit(true);
            transaction.Commit();
            return true;
        }
        catch (Exception error)
        {
            Abort(scope, transaction, "aparencia", error);
            return false;
        }
    }

    // ---------------------------------------------------------------- graphics and identity (write)

    public static bool ApplyMaterialChanges(Document doc, ElementId materialId, MaterialGraphics g)
    {
        using var transaction = new Transaction(doc, "Editar Material");
        transaction.Start();
        try
        {
            var material = (Material)doc.GetElement(materialId);
            material.Color = new Color(Convert.ToByte(g.R), Convert.ToByte(g.G), Convert.ToByte(g.B));
            material.Transparency = g.Transparency;
            material.Shininess = g.Shininess;
            material.Smoothness = g.Smoothness;
            try { material.UseRenderAppearanceForShading = g.UseRenderAppearance; }
            catch (Exception) { }
            if (g.SurfaceForegroundPatternId is not null) material.SurfaceForegroundPatternId = g.SurfaceForegroundPatternId;
            if (g.SurfaceBackgroundPatternId is not null) material.SurfaceBackgroundPatternId = g.SurfaceBackgroundPatternId;
            if (g.CutForegroundPatternId is not null) material.CutForegroundPatternId = g.CutForegroundPatternId;
            if (g.CutBackgroundPatternId is not null) material.CutBackgroundPatternId = g.CutBackgroundPatternId;
            if (g.SurfaceForegroundColor is not null) material.SurfaceForegroundPatternColor = g.SurfaceForegroundColor;
            if (g.SurfaceBackgroundColor is not null) material.SurfaceBackgroundPatternColor = g.SurfaceBackgroundColor;
            if (g.CutForegroundColor is not null) material.CutForegroundPatternColor = g.CutForegroundColor;
            if (g.CutBackgroundColor is not null) material.CutBackgroundPatternColor = g.CutBackgroundColor;
            transaction.Commit();
            return true;
        }
        catch (Exception error)
        {
            Failures.Record("graficos", error);
            transaction.RollBack();
            return false;
        }
    }

    private static readonly BuiltInParameter[] IdentityParameters =
    [
        BuiltInParameter.ALL_MODEL_DESCRIPTION, BuiltInParameter.ALL_MODEL_MANUFACTURER, BuiltInParameter.ALL_MODEL_MODEL,
        BuiltInParameter.ALL_MODEL_URL, BuiltInParameter.ALL_MODEL_MARK,
    ];

    /// <summary>The Comments parameter. It is looked up by built-in id because its name is localized.</summary>
    public static Parameter? CommentsParameter(Material material) =>
        material.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) ?? material.LookupParameter("Comments");

    public static bool ApplyIdentityChanges(Document doc, ElementId materialId, MaterialIdentity identity)
    {
        using var transaction = new Transaction(doc, "Editar Identidade");
        transaction.Start();
        try
        {
            var material = (Material)doc.GetElement(materialId);
            var name = (identity.Name ?? "").Trim();
            if (name.Length == 0)
                throw new InvalidOperationException("o nome do material nao pode ficar vazio");
            if (name != material.Name)
            {
                var taken = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).ToHashSet();
                if (taken.Contains(name))
                    throw new InvalidOperationException($"ja existe um material chamado '{name}'");
                material.Name = name;
            }
            string[] values = [identity.Description, identity.Manufacturer, identity.Model, identity.Url, identity.Mark];
            for (var i = 0; i < IdentityParameters.Length; i++)
            {
                var parameter = material.get_Parameter(IdentityParameters[i]);
                if (parameter is { IsReadOnly: false })
                    parameter.Set(values[i] ?? "");
            }
            var comments = CommentsParameter(material);
            if (comments is { IsReadOnly: false })
                comments.Set(identity.Comments ?? "");
            material.MaterialClass = identity.MaterialClass ?? "";
            material.MaterialCategory = identity.Category ?? "";
            transaction.Commit();
            return true;
        }
        catch (Exception error)
        {
            Failures.Record("identidade", error);
            transaction.RollBack();
            return false;
        }
    }

    // ---------------------------------------------------------------- duplicate

    private static string UniqueName(ISet<string> taken, string baseName)
    {
        if (!taken.Contains(baseName)) return baseName;
        var index = 2;
        while (taken.Contains($"{baseName}_{index}")) index++;
        return $"{baseName}_{index}";
    }

    private static void CopyProperties(Document doc, Material source, Material target, string appearanceName)
    {
        target.Color = source.Color;
        target.Transparency = source.Transparency;
        target.Shininess = source.Shininess;
        target.Smoothness = source.Smoothness;
        TryCopy(() => target.UseRenderAppearanceForShading = source.UseRenderAppearanceForShading);
        TryCopy(() => target.SurfaceForegroundPatternId = source.SurfaceForegroundPatternId);
        TryCopy(() => target.SurfaceBackgroundPatternId = source.SurfaceBackgroundPatternId);
        TryCopy(() => target.CutForegroundPatternId = source.CutForegroundPatternId);
        TryCopy(() => target.CutBackgroundPatternId = source.CutBackgroundPatternId);
        TryCopy(() => target.SurfaceForegroundPatternColor = source.SurfaceForegroundPatternColor);
        TryCopy(() => target.SurfaceBackgroundPatternColor = source.SurfaceBackgroundPatternColor);
        TryCopy(() => target.CutForegroundPatternColor = source.CutForegroundPatternColor);
        TryCopy(() => target.CutBackgroundPatternColor = source.CutBackgroundPatternColor);
        TryCopy(() => target.MaterialClass = source.MaterialClass);
        TryCopy(() => target.MaterialCategory = source.MaterialCategory);

        foreach (var bip in IdentityParameters)
        {
            var from = source.get_Parameter(bip);
            var to = target.get_Parameter(bip);
            if (from is not null && to is { IsReadOnly: false })
                to.Set(from.AsString() ?? "");
        }
        var fromComments = CommentsParameter(source);
        var toComments = CommentsParameter(target);
        if (fromComments is not null && toComments is { IsReadOnly: false })
            toComments.Set(fromComments.AsString() ?? "");

        if (ElementIds.IsValid(source.AppearanceAssetId) && doc.GetElement(source.AppearanceAssetId) is AppearanceAssetElement asset)
        {
            var taken = new FilteredElementCollector(doc).OfClass(typeof(AppearanceAssetElement)).Cast<Element>().Select(e => e.Name).ToHashSet();
            var copy = asset.Duplicate(UniqueName(taken, appearanceName + "_Appearance"));
            target.AppearanceAssetId = copy.Id;
        }
    }

    private static void TryCopy(Action copy)
    {
        try { copy(); }
        catch (Exception) { }
    }

    /// <summary>Creates an independent copy of a material, with its own appearance asset. Returns null and records why when it cannot.</summary>
    public static Material? DuplicateMaterial(Document doc, Material source, string newName)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0)
        {
            Failures.Record("duplicar", "o nome do novo material nao pode ficar vazio");
            return null;
        }
        var taken = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).ToHashSet();
        if (taken.Contains(newName))
        {
            Failures.Record("duplicar", $"ja existe um material chamado '{newName}'");
            return null;
        }
        using var transaction = new Transaction(doc, "Duplicar Material");
        transaction.Start();
        try
        {
            var created = (Material)doc.GetElement(Material.Create(doc, newName));
            CopyProperties(doc, source, created, newName);
            transaction.Commit();
            return created;
        }
        catch (Exception error)
        {
            Failures.Record("duplicar", error);
            transaction.RollBack();
            return null;
        }
    }

    // ---------------------------------------------------------------- paint

    /// <summary>
    /// Lets the user click faces to paint them with a material until Esc. Each click is its own undo entry.
    /// </summary>
    public static int PaintMaterialOnFaces(UIDocument uidoc, Material material, string? label, Action<string>? alert = null)
    {
        var doc = uidoc.Document;
        var painted = 0;
        label ??= material.Name;
        while (true)
        {
            Reference reference;
            try
            {
                reference = FacePicker.Pick(uidoc, new PaintFaceFilter(doc), $"Pintando: {label} | Clique em uma face. ESC para sair.");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                break;
            }
            catch (Exception error)
            {
                alert?.Invoke($"Nao foi possivel selecionar a face.\n\n{error.Message}");
                break;
            }
            using var transaction = new Transaction(doc, "Pintar Material");
            try
            {
                var element = doc.GetElement(reference);
                var face = element.GetGeometryObjectFromReference(reference) as Face;
                transaction.Start();
                doc.Paint(element.Id, face, material.Id);
                transaction.Commit();
                painted++;
            }
            catch (Exception)
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
            }
        }
        if (painted > 0)
            Notifications.Toast($"{painted} face(s) pintadas com {label}.", "Paint");
        return painted;
    }
}
