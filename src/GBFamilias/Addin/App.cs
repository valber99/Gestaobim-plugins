using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Gestaobim.Familias.Revit;

namespace Gestaobim.Familias.Addin;

/// <summary>Adiciona o painel "Famílias" a aba compartilhada "Gestão BIM" quando o Revit inicia.</summary>
public sealed class App : IExternalApplication
{
    private const string TabName = "Gestão BIM";
    private const string PanelName = "Famílias";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            LibraryService.InitSqlite();
            BuildRibbon(application);
            SelfTest.Attach(application); // so age com GB_FAMILIAS_SELFTEST definida (testes automatizados dentro do Revit)
        }
        catch (Exception error)
        {
            TaskDialog.Show("GB Famílias", $"Nao foi possivel criar o painel Famílias.\n\n{error.Message}");
            return Result.Failed;
        }
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        // Encerra o servidor de busca por IA (o gbfam.exe) junto com o Revit.
        LibraryService.Close();
        return Result.Succeeded;
    }

    private static void BuildRibbon(UIControlledApplication application)
    {
        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // A aba ja existe (outro plugin Gestão BIM a criou): o painel entra nela.
        }

        // O "Sobre" e UNICO para os tres plugins: o primeiro que inicia cria o painel "Geral"; todos se registram.
        Gestaobim.Shared.AboutHub.Register(application, TabName, "GB Famílias", UpdateNotifier.BuildAboutSection, Icon,
            Assembly.GetExecutingAssembly().Location, typeof(AboutCommand).FullName!);

        var panel = application.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName)
                    ?? application.CreateRibbonPanel(TabName, PanelName);
        var assembly = Assembly.GetExecutingAssembly().Location;

        PushButtonData Button(string name, string text, string command, string icon, string tooltip) =>
            new(name, text, assembly, command) { ToolTip = tooltip, LargeImage = Icon(icon), Image = Icon(icon) };

        panel.AddItem(Button("GBFamLibrary", "Biblioteca", typeof(OpenLibraryCommand).FullName!, "biblioteca",
            "Abre a biblioteca de famílias: busca por texto ou imagem, filtros, categorias, favoritos e coleções. Insere a família no projeto."));
        panel.AddItem(Button("GBFamSettings", "Configurações", typeof(SettingsCommand).FullName!, "config",
            "Onde estão o banco da biblioteca e a pasta das famílias, e a busca por IA."));
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
