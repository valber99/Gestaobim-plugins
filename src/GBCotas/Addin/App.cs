using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.Ai.Revit;
using Gestaobim.Cotas.Revit;

namespace Gestaobim.Cotas.Addin;

/// <summary>Cria o painel "Cotas" na aba "Gestão BIM" e o botao unico "Configurar IA".</summary>
public sealed class App : IExternalApplication
{
    private const string PanelName = AiRibbon.ConfigPanelName;
    private static AiUpdateNotifier? _updates;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            BuildRibbon(application);
            SelfTest.Attach(application);
        }
        catch (Exception error)
        {
            TaskDialog.Show("GB Cotas", $"Não foi possível criar o painel IA.\n\n{error.Message}");
            return Result.Failed;
        }
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try { Gestaobim.Ai.GatewayHost.Stop(); } catch (Exception) { }
        return Result.Succeeded;
    }

    private static void BuildRibbon(UIControlledApplication application)
    {
        AiRibbon.EnsureTab(application);
        var assembly = Assembly.GetExecutingAssembly().Location;
        _updates = new AiUpdateNotifier("GB Cotas", Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0), Icon, Details);
        Gestaobim.Shared.AboutHub.Register(application, AiRibbon.TabName, "GB Cotas", _updates.BuildAboutSection, Icon, assembly, typeof(AboutCommand).FullName!);

        AiRibbon.EnsureConfigButton(application, assembly, Icon);
        var panel = AiRibbon.Panel(application, PanelName);
        panel.AddItem(new PushButtonData("GBCotar", "Cotar\nvista", assembly, typeof(CotarCommand).FullName!)
        {
            ToolTip = "Cota a planta aberta com cotas nativas do Revit: monta as cadeias de paredes, aberturas e eixos (pelas regras ou com IA) e posiciona sem colisão.",
            LargeImage = Icon("cotar"),
            Image = Icon("cotar"),
        });
        _updates.Start(application);
    }

    private static string[] Details() => [$"Perfil de regras: {CotasPaths.ProfileFile}"];

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

[Transaction(TransactionMode.Manual)]
public sealed class CotarCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, CotasFlow.Title, CotasFlow.Run);
}

/// <summary>O botao "Sobre" unico: o primeiro plugin que iniciou fica com o comando; todos mostram a mesma janela.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Gestaobim.Shared.AboutHub.ShowAll(App.Icon);
        return Result.Succeeded;
    }
}
