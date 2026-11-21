using System.Globalization;
using Autodesk.Revit.DB;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>One layer of a wall, floor, roof or ceiling type.</summary>
public sealed class LayerRow(HostObjAttributes elementType, int index, string function, double thicknessMm, string materialName)
{
    public HostObjAttributes Type { get; } = elementType;
    public int Index { get; } = index;
    public string Function { get; } = function;
    public double ThicknessMm { get; } = thicknessMm;
    public string MaterialName { get; } = materialName;

    public string Display =>
        $"{Type.Category?.Name ?? ""} : {Type.Name}  |  Camada {Index + 1}: {Function} - " +
        $"{ThicknessMm.ToString("0.##", CultureInfo.InvariantCulture)} mm - {MaterialName}";
}

public sealed record LayerAssignResult(int Changed, IReadOnlyList<string> Failures);

/// <summary>Reads and changes the material of compound-structure layers.</summary>
public static class TypeLayers
{
    private static readonly Dictionary<string, string> FunctionNames = new()
    {
        ["Structure"] = "Estrutura",
        ["Substrate"] = "Substrato",
        ["Insulation"] = "Isolamento",
        ["Finish1"] = "Acabamento 1",
        ["Finish2"] = "Acabamento 2",
        ["Membrane"] = "Membrana",
        ["StructuralDeck"] = "Deck estrutural",
    };

    private static readonly Type[] CompoundClasses = [typeof(WallType), typeof(FloorType), typeof(RoofType), typeof(CeilingType)];

    public static List<HostObjAttributes> AllCompoundTypes(Document doc)
    {
        var types = new List<HostObjAttributes>();
        foreach (var cls in CompoundClasses)
            types.AddRange(new FilteredElementCollector(doc).OfClass(cls).Cast<HostObjAttributes>());
        return types.Where(t => t.GetCompoundStructure() is not null).ToList();
    }

    /// <summary>The distinct compound types used by the given elements, in a stable order.</summary>
    public static List<HostObjAttributes> TypesOfElements(Document doc, IEnumerable<ElementId> elementIds)
    {
        var seen = new Dictionary<long, HostObjAttributes>();
        foreach (var id in elementIds)
        {
            var element = doc.GetElement(id);
            if (element is null) continue;
            if (doc.GetElement(element.GetTypeId()) is HostObjAttributes type && type.GetCompoundStructure() is not null)
                seen[type.Id.Value] = type;
        }
        return seen.Values
            .OrderBy(t => t.Category?.Name ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<LayerRow> Layers(Document doc, IEnumerable<HostObjAttributes> types)
    {
        var rows = new List<LayerRow>();
        foreach (var type in types)
        {
            var structure = type.GetCompoundStructure();
            for (var i = 0; i < structure.LayerCount; i++)
            {
                var material = doc.GetElement(structure.GetMaterialId(i)) as Material;
                var function = structure.GetLayerFunction(i).ToString();
                rows.Add(new LayerRow(type, i, FunctionNames.GetValueOrDefault(function, function),
                    UnitUtils.ConvertFromInternalUnits(structure.GetLayerWidth(i), UnitTypeId.Millimeters),
                    material?.Name ?? "<sem material>"));
            }
        }
        return rows;
    }

    /// <summary>
    /// Sets the material on the chosen layers. One undo entry. A type Revit rejects is skipped and reported;
    /// the others are still changed.
    /// </summary>
    public static LayerAssignResult Assign(Document doc, IEnumerable<LayerRow> rows, ElementId materialId)
    {
        var byType = rows.GroupBy(r => r.Type.Id.Value).ToList();
        using var transaction = new Transaction(doc, "Aplicar material em camadas");
        transaction.Start();
        var changed = 0;
        var failures = new List<string>();
        try
        {
            foreach (var group in byType)
            {
                var type = group.First().Type;
                try
                {
                    var structure = type.GetCompoundStructure();
                    foreach (var row in group)
                        structure.SetMaterialId(row.Index, materialId);
                    type.SetCompoundStructure(structure);
                    changed += group.Count();
                }
                catch (Exception error)
                {
                    failures.Add($"{type.Name}: {error.Message}");
                }
            }
            transaction.Commit();
        }
        catch (Exception)
        {
            transaction.RollBack();
            throw;
        }
        return new LayerAssignResult(changed, failures);
    }
}
