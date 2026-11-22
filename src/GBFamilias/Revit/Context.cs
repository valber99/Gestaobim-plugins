using Autodesk.Revit.UI;

namespace Gestaobim.Familias.Revit;

/// <summary>A sessao do Revit em andamento, definida por cada comando.</summary>
public static class RevitContext
{
    private static UIApplication? _uiApplication;
    private static int _revitYear;

    public static UIApplication UiApplication
    {
        get => _uiApplication ?? throw new InvalidOperationException("Revit ainda nao foi inicializado.");
        set
        {
            _uiApplication = value;
            _revitYear = int.TryParse(value.Application.VersionNumber, out var year) ? year : 0;
        }
    }

    public static IntPtr MainWindowHandle => _uiApplication?.MainWindowHandle ?? IntPtr.Zero;

    /// <summary>Ano do Revit em execucao (2025, 2026, 2027); 0 se ainda nao se sabe.</summary>
    public static int RevitYear => _revitYear;
}
