using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.Ai.Revit;
using Gestaobim.Render.Revit;

namespace Gestaobim.Render.Addin;

/// <summary>Cria o painel "Render" na aba "Gestão BIM" e o botao unico "Configurar IA".</summary>
public sealed class App : IExternalApplication
{
    private const string PanelName = "Render";
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
            TaskDialog.Show("GB Render", $"Nao foi possivel criar o painel Render.\n\n{error.Message}");
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
        _updates = new AiUpdateNotifier("GB Render", Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0), Icon, Details);
        Gestaobim.Shared.AboutHub.Register(application, AiRibbon.TabName, "GB Render", _updates.BuildAboutSection, Icon, assembly, typeof(AboutCommand).FullName!);

        AiRibbon.EnsureConfigButton(application, assembly, Icon);
        var panel = AiRibbon.Panel(application, PanelName);
        panel.AddItem(new PushButtonData("GBRenderIa", "Render\ncom IA", assembly, typeof(RenderCommand).FullName!)
        {
            ToolTip = "Gera o render da vista 3D aberta: exporta quatro camadas alinhadas (clay, color-ID, profundidade e fundo) com um manifesto BIM e envia ao modelo de imagem.",
            LargeImage = Icon("render"),
            Image = Icon("render"),
        });
        _updates.Start(application);
    }

    private static string[] Details() => ["Conferido por teste: camadas alinhadas, todas as cores da máscara no manifesto."];

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
public sealed class RenderCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, RenderFlow.Title, RenderFlow.Run);
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
