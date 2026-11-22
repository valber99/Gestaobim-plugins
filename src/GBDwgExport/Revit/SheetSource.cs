using System.ComponentModel;
using Autodesk.Revit.DB;
using Gestaobim.DwgExport.Core;

namespace Gestaobim.DwgExport.Revit;

/// <summary>One sheet or view the user can tick in the export window.</summary>
public sealed class ExportItem(ElementId id, string number, string name, string group, bool isSheet,
    double? paperWidthMm = null, double? paperHeightMm = null, double? originXMm = null, double? originYMm = null)
    : INotifyPropertyChanged
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

    /// <summary>
    /// The sheet's lower-left corner in sheet coordinates, in millimetres. Revit writes the sheet's paper space with
    /// these same coordinates, so this is where the title block starts in the exported DWG.
    /// </summary>
    public double? OriginXMm { get; } = originXMm;

    public double? OriginYMm { get; } = originYMm;

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
                var paper = Paper(doc, sheet);
                return new ExportItem(sheet.Id, sheet.SheetNumber, sheet.Name, "", isSheet: true, paper?.Width, paper?.Height,
                    paper?.OriginX, paper?.OriginY);
            })
            .OrderBy(item => item.Number, NaturalComparer.Instance)
            .ToList();

    /// <summary>
    /// The paper a sheet uses and its lower-left corner in sheet coordinates, in millimetres. The size is snapped to a
    /// standard A paper when it is within a few millimetres of one. The title blocks are the first source: the sheet's
    /// outline also covers views parked outside the title block, which would make the paper far too big. The outline is
    /// the fallback for a sheet without a title block, and a title block type's own width and height (with no known
    /// corner) the last one. Null when Revit reports nothing usable.
    /// </summary>
    public static (double Width, double Height, double? OriginX, double? OriginY)? Paper(Document doc, ViewSheet sheet)
    {
        try
        {
            var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().ToList();

            // 1) The title blocks as drawn on the sheet.
            var boxes = titleBlocks.Select(block => block.get_BoundingBox(sheet)).Where(box => box is not null).ToList();
            if (boxes.Count > 0)
            {
                var (x0, y0) = (boxes.Min(box => box!.Min.X), boxes.Min(box => box!.Min.Y));
                var (x1, y1) = (boxes.Max(box => box!.Max.X), boxes.Max(box => box!.Max.Y));
                if (x1 > x0 && y1 > y0)
                {
                    var (width, height) = MergePlanner.SnapToStandardPaper(Millimeters(x1 - x0), Millimeters(y1 - y0));
                    return (width, height, Millimeters(x0), Millimeters(y0));
                }
            }

            // 2) The sheet's outline.
            var outline = sheet.Outline;
            if (outline.Max.U - outline.Min.U > 0 && outline.Max.V - outline.Min.V > 0)
            {
                var (width, height) = MergePlanner.SnapToStandardPaper(
                    Millimeters(outline.Max.U - outline.Min.U), Millimeters(outline.Max.V - outline.Min.V));
                return (width, height, Millimeters(outline.Min.U), Millimeters(outline.Min.V));
            }

            // 3) The largest title block type that reports its own size.
            (double Width, double Height)? best = null;
            foreach (var titleBlock in titleBlocks)
            {
                var type = doc.GetElement(titleBlock.GetTypeId());
                var typeWidth = type?.get_Parameter(BuiltInParameter.SHEET_WIDTH)?.AsDouble() ?? 0;
                var typeHeight = type?.get_Parameter(BuiltInParameter.SHEET_HEIGHT)?.AsDouble() ?? 0;
                if (typeWidth <= 0 || typeHeight <= 0) continue;
                var candidate = (Millimeters(typeWidth), Millimeters(typeHeight));
                if (best is null || candidate.Item1 * candidate.Item2 > best.Value.Width * best.Value.Height) best = candidate;
            }
            if (best is null) return null;
            var snapped = MergePlanner.SnapToStandardPaper(best.Value.Width, best.Value.Height);
            return (snapped.Width, snapped.Height, null, null);
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
