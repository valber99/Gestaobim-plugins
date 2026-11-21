using System.ComponentModel;
using Autodesk.Revit.DB;
using Gestaobim.DwgExport.Core;

namespace Gestaobim.DwgExport.Revit;

/// <summary>One sheet or view the user can tick in the export window.</summary>
public sealed class ExportItem(ElementId id, string number, string name, string group, bool isSheet) : INotifyPropertyChanged
{
    private bool _checked;

    public ElementId Id { get; } = id;
    public string Number { get; } = number;
    public string Name { get; } = name;

    /// <summary>For a view: its type ("Planta de piso", "Corte"...). For a sheet: empty.</summary>
    public string Group { get; } = group;

    public bool IsSheet { get; } = isSheet;

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
            .Select(sheet => new ExportItem(sheet.Id, sheet.SheetNumber, sheet.Name, "", isSheet: true))
            .OrderBy(item => item.Number, NaturalComparer.Instance)
            .ToList();

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
