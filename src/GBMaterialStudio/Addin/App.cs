using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Gestaobim.MaterialStudio.Core;

namespace Gestaobim.MaterialStudio.Addin;

/// <summary>Creates the "Gestão BIM" tab and its "Materiais" panel when Revit starts.</summary>
public sealed class App : IExternalApplication
{
    private const string TabName = "Gestão BIM";
    private const string PanelName = "Materiais";

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            StoragePaths.MigrateFromPyRevit();
        }
        catch (Exception)
        {
            // Migration is a convenience; the plugin works without the old files.
        }

        try
        {
            BuildRibbon(application);
        }
        catch (Exception error)
        {
            TaskDialog.Show("Gestão BIM", $"Nao foi possivel criar a aba Gestão BIM.\n\n{error.Message}");
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
            // The tab already exists (another Gestão BIM plugin created it): the panel goes there.
        }

        var panel = application.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName)
                    ?? application.CreateRibbonPanel(TabName, PanelName);

        var assembly = Assembly.GetExecutingAssembly().Location;

        PushButtonData Button(string name, string text, string command, string icon, string tooltip) =>
            new(name, text, assembly, command)
            {
                ToolTip = tooltip,
                LargeImage = Icon(icon),
                Image = Icon(icon),
            };

        panel.AddItem(Button("GBEditMaterial", "Editar\nmaterial", typeof(EditMaterialCommand).FullName!, "editar",
            "Selecione uma face para editar cor, padrões, aparência e texturas do material. Alterações são aplicadas pelos botões do editor e podem ser desfeitas no Revit."));
        panel.AddItem(Button("GBEditMaterialFixed", "Editor\nfixo", typeof(EditMaterialFixedCommand).FullName!, "editor-fixo",
            "Selecione uma face e abra o editor numa janela que fica ao lado do Revit: continue navegando e selecionando enquanto edita. Cada aplicação é um único Ctrl+Z."));
        panel.AddItem(Button("GBFavorites", "Favoritos", typeof(FavoritesCommand).FullName!, "favoritos",
            "Abre a biblioteca de favoritos. Pesquise, abra ou aplique um material nas faces do modelo. Pressione Esc para encerrar a pintura."));
        panel.AddSeparator();
        panel.AddItem(Button("GBOrganize", "Organizar\nmateriais", typeof(OrganizeCommand).FullName!, "organizar",
            "Veja onde cada material é usado, encontre duplicados, exclua os não usados (mesma segurança do Purge do Revit) e renomeie em lote."));
        panel.AddItem(Button("GBApplyToType", "Aplicar em\ntipo", typeof(ApplyToTypeCommand).FullName!, "aplicar-tipo",
            "Troque o material de camadas de tipos de parede, piso, cobertura e forro. Use a seleção atual ou escolha os tipos numa lista; o destino pode ser um material do projeto ou um favorito."));
    }

    private static BitmapImage Icon(string name)
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
