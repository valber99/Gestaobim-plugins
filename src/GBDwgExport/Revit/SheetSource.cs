using System.ComponentModel;
using Autodesk.Revit.DB;
using Gestaobim.DwgExport.Core;

namespace Gestaobim.DwgExport.Revit;

/// <summary>One sheet or view the user can tick in the export window.</summary>
public sealed class ExportItem(ElementId id, string number, string name, string group, bool isSheet,
    double? paperWidthMm = null, double? paperHeightMm = null) : INotifyPropertyChanged
{
    private bool _checked;

    public ElementId Id { get; } = id;
    public string Number { get; } = number;
    public string Name { get; } = name;

    /// <summary>For a view: its type ("Planta de piso", "Corte"...). For a sheet: empty.</summary>
    public string Group { get; } = group;

    public bool IsSheet { get; } = isSheet;

    /// <summary>The paper the sheet uses in Revit, in millimetres (from its title block), when known.</summary>
    public double? PaperWidthMm { get; } = paperWidthMm;

    public double? PaperHeightMm { get; } = paperHeightMm;

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Collects the sheets and the exportable views of a project, in natural order.</summary>
public static class SheetSource
{
    private static readonly HashSet<ViewType> ExportableViews =
    [
        ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation, ViewType.Section, ViewType.Detail,
        ViewType.ThreeD, ViewType.DraftingView, ViewType.AreaPlan, ViewType.EngineeringPlan,
    ];

    public static List<ExportItem> Sheets(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(sheet => !sheet.IsPlaceholder)
            .Select(sheet =>
            {
                var paper = PaperSize(doc, sheet);
                return new ExportItem(sheet.Id, sheet.SheetNumber, sheet.Name, "", isSheet: true, paper?.Width, paper?.Height);
            })
            .OrderBy(item => item.Number, NaturalComparer.Instance)
            .ToList();

    /// <summary>
    /// The paper a sheet uses, in millimetres, snapped to a standard A paper when it is within a few millimetres of one.
    /// The sheet's outline is the first source: it covers every title block on the sheet. A title block type that
    /// reports its own width and height is the fallback. Null when Revit reports nothing usable.
    /// </summary>
    public static (double Width, double Height)? PaperSize(Document doc, ViewSheet sheet)
    {
        try
        {
            var outline = sheet.Outline;
            var width = outline.Max.U - outline.Min.U;
            var height = outline.Max.V - outline.Min.V;
            if (width > 0 && height > 0) return MergePlanner.SnapToStandardPaper(Millimeters(width), Millimeters(height));

            (double Width, double Height)? best = null;
            foreach (var titleBlock in new FilteredElementCollector(doc, sheet.Id)
                         .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                var type = doc.GetElement(titleBlock.GetTypeId());
                var typeWidth = type?.get_Parameter(BuiltInParameter.SHEET_WIDTH)?.AsDouble() ?? 0;
                var typeHeight = type?.get_Parameter(BuiltInParameter.SHEET_HEIGHT)?.AsDouble() ?? 0;
                if (typeWidth <= 0 || typeHeight <= 0) continue;
                var candidate = (Millimeters(typeWidth), Millimeters(typeHeight));
                if (best is null || candidate.Item1 * candidate.Item2 > best.Value.Width * best.Value.Height) best = candidate;
            }
            return best is null ? null : MergePlanner.SnapToStandardPaper(best.Value.Width, best.Value.Height);
        }
        catch (Exception)
        {
            // Without a paper size the planner falls back to its default paper.
            return null;
        }
    }

    private static double Millimeters(double feet) => Math.Round(UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters), 1);

    public static List<ExportItem> Views(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(view => !view.IsTemplate && view is not ViewSheet && ExportableViews.Contains(view.ViewType) && view.CanBePrinted)
            .Select(view => new ExportItem(view.Id, "", view.Name, view.ViewType.ToString(), isSheet: false))
            .OrderBy(item => item.Group, NaturalComparer.Instance)
            .ThenBy(item => item.Name, NaturalComparer.Instance)
            .ToList();

    public static IReadOnlyList<string> RevitSetups(Document doc)
    {
        try
        {
            return BaseExportOptions.GetPredefinedSetupNames(doc).OrderBy(name => name, NaturalComparer.Instance).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
