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

        panel.AddItem(Button("GBDwgSheets", "Exportar\nfolhas", typeof(ExportSheetsCommand).FullName!, "exportar-folhas",
            "Exporta as folhas escolhidas para um só DWG, com um layout por folha, no mesmo papel que a folha usa no Revit. Não precisa do AutoCAD."));
        panel.AddItem(Button("GBDwgModel", "Model\nspace", typeof(ModelSpaceCommand).FullName!, "model-space",
            "Junta as folhas escolhidas lado a lado no model space de um só desenho. Não precisa do AutoCAD."));
        panel.AddItem(Button("GBDwgSingle", "Layout\núnico", typeof(SingleLayoutCommand).FullName!, "layout-unico",
            "Junta todas as folhas escolhidas num único layout. Não precisa do AutoCAD."));
        panel.AddItem(Button("GBDwgViews", "Exportar\nvistas", typeof(ExportViewsCommand).FullName!, "exportar-vistas",
            "Exporta vistas (plantas, cortes, elevações, 3D...) para DWG ou DXF, e não só folhas."));
        panel.AddSeparator();
        var configMenu = (PulldownButton)panel.AddItem(new PulldownButtonData("GBDwgSettings", "Configurações")
        {
            ToolTip = "Exportar ou importar as configurações do GB DWG Export (.zip).",
            LargeImage = Icon("config-exportar"),
            Image = Icon("config-exportar"),
        });
        configMenu.AddPushButton(Button("GBDwgSettingsOut", "Exportar configurações", typeof(ExportSettingsCommand).FullName!, "config-exportar",
            "Salva as configurações do GB DWG Export num arquivo .zip para levar a outra máquina."));
        configMenu.AddPushButton(Button("GBDwgSettingsIn", "Importar configurações", typeof(ImportSettingsCommand).FullName!, "config-importar",
            "Lê um .zip de configurações do GB DWG Export."));
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
