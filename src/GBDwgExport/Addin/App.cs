using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace Gestaobim.DwgExport.Addin;

/// <summary>Adds the "Exportar DWG" panel to the shared "Gestão BIM" tab when Revit starts.</summary>
public sealed class App : IExternalApplication
{
    private const string TabName = "Gestão BIM";
    private const string PanelName = "Exportar DWG";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            BuildRibbon(application);
            Revit.SelfTest.Attach(application); // so age com GB_DWG_EXPORT_SELFTEST definida (teste automatizado no Revit)
        }
        catch (Exception error)
        {
            TaskDialog.Show("GB DWG Export", $"Nao foi possivel criar o painel Exportar DWG.\n\n{error.Message}");
            return Result.Failed;
        }
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static void BuildRibbon(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // The tab already exists (GB Material Studio created it): the panel goes there.
        }

        // O "Sobre" e UNICO para os tres plugins: o primeiro que inicia cria o painel "Geral"; todos se registram.
        Gestaobim.Shared.AboutHub.Register(application, TabName, "GB DWG Export", UpdateNotifier.BuildAboutSection, Icon,
            Assembly.GetExecutingAssembly().Location, typeof(AboutCommand).FullName!);

        var panel = application.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName)
                    ?? application.CreateRibbonPanel(TabName, PanelName);
        var assembly = Assembly.GetExecutingAssembly().Location;

        PushButtonData Button(string name, string text, string command, string icon, string tooltip) =>
            new(name, text, assembly, command) { ToolTip = tooltip, LargeImage = Icon(icon), Image = Icon(icon) };

        panel.AddItem(Button("GBDwgExport", "Exportar\nDWG", typeof(ExportSheetsCommand).FullName!, "exportar-folhas",
            "Abre a interface do GB DWG Export: exporte folhas ou vistas para DWG/DXF (múltiplos layouts, model space, layout único ou arquivos separados) e gerencie configurações."));
        UpdateNotifier.Start(application);
    }

    internal static BitmapImage Icon(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Icons.{name}.png")
                           ?? throw new InvalidOperationException($"Icone nao encontrado: {name}");
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
