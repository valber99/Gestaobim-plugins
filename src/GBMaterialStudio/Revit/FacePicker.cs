using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>Picks a face with the eyedropper cursor.</summary>
public static class FacePicker
{
    private static string CursorFile =>
        Path.Combine(Path.GetDirectoryName(typeof(FacePicker).Assembly.Location)!, "eyedropper.cur");

    /// <summary>
    /// Runs PickObject with the eyedropper cursor. Throws <see cref="Autodesk.Revit.Exceptions.OperationCanceledException"/>
    /// when the user presses Esc. If the cursor cannot be installed, Revit's own cursor is used.
    /// </summary>
    public static Reference Pick(UIDocument uidoc, ISelectionFilter filter, string prompt)
    {
        var installed = false;
        System.Windows.Forms.Timer? timer = null;
        try
        {
            if (File.Exists(CursorFile))
                installed = CursorHook.Install(CursorFile);
            if (installed)
            {
                timer = new System.Windows.Forms.Timer { Interval = 40 };
                timer.Tick += (_, _) => CursorHook.Enforce();
                timer.Start();
            }
        }
        catch
        {
            installed = false;
        }
        try
        {
            return uidoc.Selection.PickObject(ObjectType.Face, filter, prompt);
        }
        finally
        {
            timer?.Stop();
            timer?.Dispose();
            if (installed)
                CursorHook.Uninstall();
        }
    }
}
