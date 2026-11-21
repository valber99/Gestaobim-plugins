using Autodesk.Revit.UI;

namespace Gestaobim.DwgExport.Revit;

/// <summary>The running Revit session, set by every command.</summary>
public static class RevitContext
{
    private static UIApplication? _uiApplication;

    public static UIApplication UiApplication
    {
        get => _uiApplication ?? throw new InvalidOperationException("Revit ainda nao foi inicializado.");
        set => _uiApplication = value;
    }

    public static IntPtr MainWindowHandle => _uiApplication?.MainWindowHandle ?? IntPtr.Zero;
}
