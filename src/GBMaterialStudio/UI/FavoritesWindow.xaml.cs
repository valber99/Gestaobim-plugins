using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.MaterialStudio.Revit;

namespace Gestaobim.MaterialStudio.UI;

/// <summary>A favorite in the browser list, with a preview of its texture or colour.</summary>
public sealed class PreviewItem
{
    public PreviewItem(FavoriteItem item)
    {
        Item = item;
        Data = item.Data;
        Display = item.Display;
        Name = item.Name;
        Source = item.Source;
        PreviewBrush = BuildPreview();
    }

    public FavoriteItem Item { get; }
    public JsonObject Data { get; }
    public string Display { get; }
    public string Name { get; }
    public string Source { get; }
    public string PreviewKind { get; private set; } = "Cor do material";
    public Brush PreviewBrush { get; }

    private static string Text(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int Channel(JsonNode? color, string key)
    {
        if (color is JsonObject o && o[key] is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return Math.Clamp(i, 0, 255);
            if (v.TryGetValue<double>(out var d)) return Math.Clamp((int)d, 0, 255);
        }
        return 160;
    }

    private Brush BuildPreview()
    {
        var path = Text(Data["appearance"], "texture_path");
        try
        {
            var resolved = string.IsNullOrEmpty(path) ? "" : TexturePaths.Resolve(path);
            if (resolved.Length > 0 && File.Exists(resolved))
            {
                var bitmap = new BitmapImage();
                using (var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 96;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                brush.Freeze();
                PreviewKind = "Textura do material";
                return brush;
            }
        }
        catch (Exception)
        {
            // Falls through to the colour sample.
        }
        if (path.Length > 0) PreviewKind = "Textura indisponivel - amostra de cor";
        var color = (Data["graphics"] as JsonObject)?["color"];
        return Visuals.ToBrush(Visuals.Hex(Channel(color, "r"), Channel(color, "g"), Channel(color, "b")));
    }
}

/// <summary>Browse, open or paint with favorite materials.</summary>
public partial class FavoritesWindow : Window
{
    private readonly UIDocument _uidoc;
    private List<PreviewItem> _items = [];
    private string _note = "";

    /// <summary>The material to work with after the window closes, and whether to paint it onto faces.</summary>
    public Material? ChosenMaterial { get; private set; }
    public bool PaintAfter { get; private set; }

    public FavoritesWindow(UIDocument uidoc)
    {
        _uidoc = uidoc;
        try
        {
            var (copied, errors) = Revit.Favorites.SyncToLibrary(RevitContext.Application);
            if (copied > 0) _note = $"{copied} favorito(s) copiado(s) para a biblioteca portatil.";
            if (errors.Count > 0) _note = $"Biblioteca: {errors[0]}";
        }
        catch (Exception error)
        {
            _note = $"Biblioteca portatil indisponivel: {error.Message}";
        }
        InitializeComponent();
        Search.TextChanged += (_, _) => ApplyFilter();
        Favorites.SelectionChanged += (_, _) => UpdateDetails(Favorites.SelectedItem as PreviewItem);
        Refresh.Click += (_, _) => Reload();
        Open.Click += (_, _) => Choose(paint: false);
        ApplyFaces.Click += (_, _) => Choose(paint: true);
        CloseButton.Click += (_, _) => Close();
        Reload();
        Dialogs.Attach(this);
    }

    private void ApplyFilter()
    {
        var text = Search.Text.Trim();
        var visible = _items.Where(i => i.Display.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        Favorites.ItemsSource = visible;
        if (_items.Count == 0)
            Status.Text = "Sua biblioteca esta vazia. Abra um material e clique em Favoritar para salva-lo aqui.";
        else if (visible.Count == 0)
            Status.Text = "Nenhum material corresponde a busca. Limpe o campo para ver todos os favoritos.";
        else
            Status.Text = _note.Length > 0 ? _note : $"{visible.Count} favorito(s). Itens \"na biblioteca\" transferem sem abrir a origem.";
        UpdateDetails(null);
    }

    private void Reload()
    {
        try
        {
            _items = Revit.Favorites.List().Select(f => new PreviewItem(f)).ToList();
            ApplyFilter();
        }
        catch (Exception error)
        {
            _items = [];
            Favorites.ItemsSource = new List<PreviewItem>();
            UpdateDetails(null);
            Status.Text = $"Nao foi possivel ler a biblioteca. Nenhum dado foi alterado: {error.Message}";
        }
    }

    private static string Text(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private void UpdateDetails(PreviewItem? item)
    {
        var enabled = item is not null;
        Open.IsEnabled = enabled;
        ApplyFaces.IsEnabled = enabled;
        DetailsTabs.IsEnabled = enabled;
        SelectedName.Text = item?.Name ?? "Selecione um favorito";
        SelectedPreviewKind.Text = item?.PreviewKind ?? "A previa e os detalhes aparecem aqui.";
        var empty = Visuals.ToBrush("#E8EDEF");
        SelectedPreview.Background = item?.PreviewBrush ?? empty;
        DetailTexturePreview.Background = item?.PreviewBrush ?? empty;

        var data = item?.Data;
        var identity = data?["identity"];
        var appearance = data?["appearance"];
        DetailName.Text = Text(data, "name");
        DetailClass.Text = Text(identity, "material_class");
        DetailDescription.Text = Text(identity, "description");
        DetailTexturePath.Text = Text(appearance, "texture_path");
        DetailSource.Text = Text(data, "source_doc");
        DetailSourcePath.Text = Text(data, "source_path");
        DetailTextureStatus.Text = item?.PreviewKind ?? "";

        if (item is null) DetailTransferStatus.Text = "";
        else if (Text(data, "library_uid").Length > 0)
            DetailTransferStatus.Text = "Na biblioteca portatil: transfere para qualquer projeto, sem abrir a origem.";
        else if (Text(data, "source_key").Length > 0 || Text(data, "project_id").Length > 0)
            DetailTransferStatus.Text = "Ainda fora da biblioteca. Abra o projeto de origem e reabra os favoritos para copiar.";
        else
            DetailTransferStatus.Text = "Registro legado parcial. Favorite novamente no projeto de origem para transferir com seguranca.";
    }

    private void Choose(bool paint)
    {
        if (Favorites.SelectedItem is not PreviewItem item) return;
        try
        {
            var result = Revit.Favorites.GetOrCreateMaterial(RevitContext.Application, _uidoc.Document, item.Data);
            if (result.MissingTextures.Count > 0)
                Notifications.Toast($"Material transferido, mas {result.MissingTextures.Count} textura(s) nao foram encontradas neste computador.", "Favoritos");
            ChosenMaterial = result.Material;
            PaintAfter = paint;
            Close();
        }
        catch (Exception error)
        {
            Dialogs.Alert(error.Message, "Gestão BIM | Favoritos");
        }
    }

    /// <summary>Opens the browser and then, depending on the choice, edits or paints the favorite.</summary>
    public static void Run(UIDocument uidoc)
    {
        var window = new FavoritesWindow(uidoc);
        window.ShowDialog();
        if (window.ChosenMaterial is null) return;

        if (window.PaintAfter)
        {
            MaterialServices.PaintMaterialOnFaces(uidoc, window.ChosenMaterial, window.ChosenMaterial.Name,
                message => Dialogs.Alert(message, "Paint"));
            return;
        }
        var outcome = MaterialEditorWindow.Show(uidoc, window.ChosenMaterial, modeless: false);
        if (outcome.OpenNative)
            MaterialEditorWindow.OpenNativeMaterialBrowser(uidoc);
    }
}
