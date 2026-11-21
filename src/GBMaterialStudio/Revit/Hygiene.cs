using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Gestaobim.MaterialStudio.Core;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>One material in the organizer list.</summary>
public sealed class MaterialRow : INotifyPropertyChanged
{
    private string _newName = "";
    private string _state = "";

    public MaterialRow(Material material, int usage, bool protectedByRevit)
    {
        Id = material.Id;
        Key = material.Id.Value;
        Name = material.Name;
        MaterialClass = material.MaterialClass ?? "";
        Usage = usage;
        UsageText = protectedByRevit ? "em uso (Revit)" : usage > 0 ? usage.ToString(CultureInfo.InvariantCulture) : "nao usado";
    }

    public ElementId Id { get; }
    public long Key { get; }
    public string Name { get; }
    public string MaterialClass { get; }
    public int Usage { get; }
    public string UsageText { get; }
    public string DuplicateOf { get; set; } = "";

    public string NewName
    {
        get => _newName;
        set { _newName = value; Raise(); }
    }

    public string State
    {
        get => _state;
        set { _state = value; Raise(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failures, int AssetsDeleted);

public static class Hygiene
{
    // ---------------------------------------------------------------- signature

    private static string PatternName(Document doc, ElementId id)
    {
        if (!ElementIds.IsValid(id)) return "";
        try
        {
            return (doc.GetElement(id) as FillPatternElement)?.GetFillPattern()?.Name ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string ColorText(Color? color) =>
        color is null || !color.IsValid ? "-" : $"{color.Red},{color.Green},{color.Blue}";

    /// <summary>
    /// Text describing what a material looks like, independent of ids and of the project. Two materials
    /// with the same signature render and draw the same way.
    /// </summary>
    public static string Signature(Document doc, Material material)
    {
        var parts = new List<string>
        {
            ColorText(material.Color), material.Transparency.ToString(CultureInfo.InvariantCulture),
            material.Shininess.ToString(CultureInfo.InvariantCulture), material.Smoothness.ToString(CultureInfo.InvariantCulture),
            material.MaterialClass ?? "", material.MaterialCategory ?? "",
            PatternName(doc, material.SurfaceForegroundPatternId), ColorText(material.SurfaceForegroundPatternColor),
            PatternName(doc, material.SurfaceBackgroundPatternId), ColorText(material.SurfaceBackgroundPatternColor),
            PatternName(doc, material.CutForegroundPatternId), ColorText(material.CutForegroundPatternColor),
            PatternName(doc, material.CutBackgroundPatternId), ColorText(material.CutBackgroundPatternColor),
        };

        var asset = ElementIds.IsValid(material.AppearanceAssetId)
            ? (doc.GetElement(material.AppearanceAssetId) as AppearanceAssetElement)?.GetRenderingAsset()
            : null;
        if (asset is not null)
        {
            parts.Add(AssetProps.BaseSchema(asset) ?? "");
            foreach (var name in new[] { "generic_diffuse", "ceramic_color", "metal_color", "glass_color",
                                         "generic_roughness", "generic_reflectivity", "generic_transparency" })
            {
                var property = asset.FindByName(name);
                if (property is null) continue;
                if (AssetProps.TryGetColor(property, out var color)) parts.Add(ColorText(color));
                else if (AssetProps.TryGetDouble(property, out var value)) parts.Add(value.ToString("0.000", CultureInfo.InvariantCulture));
                else parts.Add("?");
            }
            var textures = MaterialServices.CollectAssetTextures(asset);
            foreach (var role in new[] { TextureRole.Bump, TextureRole.Cutout, TextureRole.Main, TextureRole.Transparency })
                parts.Add((textures.GetValueOrDefault(role) ?? "").ToLowerInvariant());
        }
        return string.Join("|", parts);
    }

    public static string SignatureHash(Document doc, Material material) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Signature(doc, material)))).ToLowerInvariant();

    // ---------------------------------------------------------------- usage

    /// <summary>
    /// Material ids held in an element's ElementId parameters. GetMaterialIds does not report these (family
    /// material parameters, for example), so a purge based on it alone would delete materials in use.
    /// </summary>
    private static HashSet<long> ReferencedByParameters(Element element, ISet<long> materialValues)
    {
        var found = new HashSet<long>();
        foreach (Parameter parameter in element.Parameters)
        {
            try
            {
                if (parameter.StorageType != StorageType.ElementId) continue;
                var value = parameter.AsElementId().Value;
                if (materialValues.Contains(value)) found.Add(value);
            }
            catch (Exception)
            {
                // A parameter that cannot be read cannot hold a usable reference.
            }
        }
        return found;
    }

    private static void Bump(Dictionary<long, int> counts, IEnumerable<long> values)
    {
        foreach (var value in values)
            counts[value] = counts.GetValueOrDefault(value) + 1;
    }

    /// <summary>How many elements and types reference each material (keyed by id value).</summary>
    public static Dictionary<long, int> CollectUsage(Document doc, ISet<long> materialValues)
    {
        var counts = new Dictionary<long, int>();

        foreach (var element in new FilteredElementCollector(doc).WhereElementIsElementType())
        {
            var used = new HashSet<long>();
            try { used.UnionWith(element.GetMaterialIds(false).Select(i => i.Value)); }
            catch (Exception) { }
            used.UnionWith(ReferencedByParameters(element, materialValues));
            Bump(counts, used);
        }

        foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            var used = new HashSet<long>();
            try
            {
                used.UnionWith(element.GetMaterialIds(false).Select(i => i.Value));
                used.UnionWith(element.GetMaterialIds(true).Select(i => i.Value));
            }
            catch (Exception) { }
            if (element is FamilyInstance)
                used.UnionWith(ReferencedByParameters(element, materialValues));
            Bump(counts, used);
        }

        foreach (Category category in doc.Settings.Categories)
        {
            foreach (var current in new[] { category }.Concat(category.SubCategories.Cast<Category>()))
            {
                try
                {
                    if (current.Material is not null)
                        Bump(counts, [current.Material.Id.Value]);
                }
                catch (Exception) { }
            }
        }
        return counts;
    }

    /// <summary>
    /// Ids Revit's own Purge Unused would remove. A material only counts as unused when this and the
    /// module's own scan agree: Revit sees uses the scan cannot (nested families, defaults).
    /// </summary>
    public static HashSet<long> RevitUnusedIds(Document doc) =>
        doc.GetUnusedElements(new HashSet<ElementId>()).Select(i => i.Value).ToHashSet();

    /// <summary>One row per material, with usage and the material each one duplicates.</summary>
    public static List<MaterialRow> Analyze(Document doc)
    {
        var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
        var usage = CollectUsage(doc, materials.Select(m => m.Id.Value).ToHashSet());
        var purgeable = RevitUnusedIds(doc);

        var rows = new Dictionary<long, MaterialRow>();
        var candidates = new List<Candidate>();
        foreach (var material in materials)
        {
            var key = material.Id.Value;
            var count = usage.GetValueOrDefault(key);
            var protectedByRevit = count == 0 && !purgeable.Contains(key);
            var row = new MaterialRow(material, protectedByRevit ? 1 : count, protectedByRevit);
            rows[key] = row;
            try
            {
                candidates.Add(new Candidate(key, material.Name, Signature(doc, material), row.Usage));
            }
            catch (Exception)
            {
                // A material whose appearance cannot be read is simply not compared.
            }
        }
        foreach (var group in DuplicateFinder.Group(candidates))
            foreach (var duplicate in group.Duplicates)
                rows[duplicate.Key].DuplicateOf = group.Primary.Name;
        return rows.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- change

    /// <summary>
    /// Deletes unused materials and the appearance assets nobody else uses. Only rows with no usage are
    /// touched. A deletion that would remove anything besides the material is undone. One undo entry.
    /// </summary>
    public static DeleteResult DeleteUnused(Document doc, IEnumerable<MaterialRow> rows)
    {
        var targets = rows.Where(r => r.Usage == 0).ToList();
        if (targets.Count == 0) return new DeleteResult([], [], 0);

        using var transaction = new Transaction(doc, "Excluir materiais nao usados");
        transaction.Start();
        var deleted = new List<string>();
        var failures = new List<string>();
        var assetIds = new HashSet<ElementId>();
        try
        {
            foreach (var row in targets)
            {
                var material = doc.GetElement(row.Id) as Material;
                if (material is null) continue;
                if (ElementIds.IsValid(material.AppearanceAssetId)) assetIds.Add(material.AppearanceAssetId);

                using var guard = new SubTransaction(doc);
                guard.Start();
                try
                {
                    var removed = doc.Delete(row.Id);
                    if (removed.Count != 1)
                    {
                        guard.RollBack();
                        failures.Add($"{row.Name}: a exclusao removeria outros {removed.Count - 1} elemento(s)");
                        continue;
                    }
                    guard.Commit();
                    deleted.Add(row.Name);
                }
                catch (Exception error)
                {
                    guard.RollBack();
                    failures.Add($"{row.Name}: {error.Message}");
                }
            }

            var inUse = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .Select(m => m.AppearanceAssetId.Value).ToHashSet();
            var assetsDeleted = 0;
            foreach (var assetId in assetIds)
            {
                if (inUse.Contains(assetId.Value) || doc.GetElement(assetId) is null) continue;
                try
                {
                    doc.Delete(assetId);
                    assetsDeleted++;
                }
                catch (Exception)
                {
                    // An asset that cannot be deleted is left in place.
                }
            }
            transaction.Commit();
            return new DeleteResult(deleted, failures, assetsDeleted);
        }
        catch (Exception)
        {
            transaction.RollBack();
            throw;
        }
    }

    /// <summary>
    /// Applies planned renames in two phases, so swaps and chains never collide. Only steps with status Ok
    /// are applied. One undo entry. Returns how many were renamed.
    /// </summary>
    public static int Rename(Document doc, IEnumerable<RenameStep> steps, IEnumerable<MaterialRow> rows)
    {
        var todo = steps.Where(s => s.Status == RenameStatus.Ok).ToList();
        if (todo.Count == 0) return 0;
        var ids = rows.ToDictionary(r => r.Key, r => r.Id);

        using var transaction = new Transaction(doc, "Renomear materiais");
        transaction.Start();
        try
        {
            var elements = todo.Select(step => (Element: doc.GetElement(ids[step.Key]), Step: step)).ToList();
            foreach (var (element, step) in elements)
                if (element is null)
                    throw new InvalidOperationException($"O material {step.Old} nao existe mais. Atualize a lista.");
            for (var i = 0; i < elements.Count; i++)
                elements[i].Element.Name = $"__gb_tmp_{i}";
            foreach (var (element, step) in elements)
                element.Name = step.New;
            transaction.Commit();
            return todo.Count;
        }
        catch (Exception)
        {
            transaction.RollBack();
            throw;
        }
    }
}
