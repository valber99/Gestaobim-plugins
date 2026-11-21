using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>The running Revit session, set by every command and by the ExternalEvent dispatcher.</summary>
public static class RevitContext
{
    private static UIApplication? _uiApplication;

    public static UIApplication UiApplication
    {
        get => _uiApplication ?? throw new InvalidOperationException("Revit ainda nao foi inicializado.");
        set => _uiApplication = value;
    }

    public static Application Application => UiApplication.Application;

    public static string Version => Application.VersionNumber;

    public static IntPtr MainWindowHandle => UiApplication.MainWindowHandle;
}

/// <summary>Failure reasons recorded by the apply operations, so the UI can say why something did not apply.</summary>
public static class Failures
{
    private static readonly List<string> Reasons = [];

    public static void Record(string step, object error) => Reasons.Add($"{step}: {(error is Exception e ? e.Message : error)}");

    public static IReadOnlyList<string> Pop()
    {
        var copy = Reasons.ToList();
        Reasons.Clear();
        return copy;
    }
}

public static class ElementIds
{
    /// <summary>The integer behind an ElementId (a long since Revit 2024).</summary>
    public static long Value(ElementId id) => id.Value;

    public static bool IsValid(ElementId? id) => id is not null && id != ElementId.InvalidElementId;
}
