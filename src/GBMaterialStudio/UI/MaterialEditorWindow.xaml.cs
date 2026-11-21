using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.MaterialStudio.Core;
using Gestaobim.MaterialStudio.Revit;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Visibility = System.Windows.Visibility;
using ComboBox = System.Windows.Controls.ComboBox;
using RevitColor = Autodesk.Revit.DB.Color;

namespace Gestaobim.MaterialStudio.UI;

/// <summary>What the editor asked the caller to do once it closed.</summary>
public sealed class EditorRequest
{
    public bool OpenNative { get; set; }
    public Material? NextMaterial { get; set; }
    public (Material Material, string Label, string Message)? Paint { get; set; }
}

/// <summary>The material editor: graphics, render appearance, textures, identity and favorites.</summary>
public partial class MaterialEditorWindow : Window
{
    private const string TextureExtensions = "jpg;png;bmp;tif;tga;exr;hdr";
    private static MaterialEditorWindow? _held;

    private readonly UIDocument _uidoc;
    private readonly Document _doc;
    private readonly Material _material;
    private readonly bool _modeless;
    private readonly EditorRequest _request = new();

    private int _r, _g, _b, _transparency, _shininess, _smoothness;
    private RevitColor? _surfaceFg, _surfaceBg, _cutFg, _cutBg;
    private AppearanceData _app;

    private readonly PatternItem _none = PatternItem.None();
    private readonly List<PatternItem> _patterns;
    private readonly List<PatternItem> _cutPatterns;

    /// <summary>
    /// Opens the editor. Modal blocks until it closes and returns what the user asked for. Modeless returns at
    /// once and keeps the window open beside Revit; every handler that touches the model then runs in an
    /// ExternalEvent, because Revit only accepts API calls inside a valid context.
    /// </summary>
    public static EditorRequest Show(UIDocument uidoc, Material material, bool modeless)
    {
        var window = new MaterialEditorWindow(uidoc, material, modeless);
        Dialogs.Attach(window);
        if (!modeless)
        {
            window.ShowDialog();
            return window.Finish();
        }
        try { _held?.Close(); }
        catch (InvalidOperationException) { }
        _held = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_held, window)) _held = null;
            RevitDispatcher.Post(() => window.Finish());
        };
        window.Show();
        return window._request;
    }

    public static void OpenNativeMaterialBrowser(UIDocument uidoc)
    {
        var command = RevitCommandId.LookupPostableCommandId(PostableCommand.Materials);
        if (command is not null)
            uidoc.Application.PostCommand(command);
    }

    private EditorRequest Finish()
    {
        if (_request.NextMaterial is not null)
            return Show(_uidoc, _request.NextMaterial, _modeless);
        if (_request.Paint is { } paint)
        {
            Notifications.Toast(paint.Message, "Paint");
            MaterialServices.PaintMaterialOnFaces(_uidoc, paint.Material, paint.Label, message => Dialogs.Alert(message, "Paint"));
        }
        if (_modeless && _request.OpenNative)
            OpenNativeMaterialBrowser(_uidoc);
        return _request;
    }

    /// <summary>Runs an action that touches the model: directly when modal, queued to Revit when modeless.</summary>
    private void Run(Action action)
    {
        if (_modeless) RevitDispatcher.Post(action);
        else action();
    }

    private MaterialEditorWindow(UIDocument uidoc, Material material, bool modeless)
    {
        _uidoc = uidoc;
        _doc = uidoc.Document;
        _material = material;
        _modeless = modeless;
        if (modeless) _ = RevitDispatcher.Instance;

        _r = material.Color.Red;
        _g = material.Color.Green;
        _b = material.Color.Blue;
        _transparency = material.Transparency;
        _shininess = material.Shininess;
        _smoothness = material.Smoothness;
        _surfaceFg = material.SurfaceForegroundPatternColor;
        _surfaceBg = material.SurfaceBackgroundPatternColor;
        _cutFg = material.CutForegroundPatternColor;
        _cutBg = material.CutBackgroundPatternColor;
        _app = MaterialServices.ReadAppearanceEditable(_doc, material);

        var all = MaterialServices.GetFillPatterns(_doc);
        _patterns = [_none, .. all];
        _cutPatterns = [_none, .. all.Where(p => p.Drafting)];

        InitializeComponent();
        Populate();
        Wire();
    }

    // ------------------------------------------------------------ small helpers

    private static string Pct(double value) => value.ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string Hex(RevitColor color) => Visuals.Hex(color.Red, color.Green, color.Blue);

    private PatternItem SelectedPattern(ComboBox combo) => combo.SelectedItem as PatternItem ?? _none;

    private void SetSwatch(System.Windows.Controls.Border swatch, RevitColor? color) =>
        swatch.Background = Visuals.ToBrush(color is null ? "#FFFFFF" : Hex(color));

    private void UpdatePatternControls(ComboBox combo, System.Windows.Controls.Border preview, Button button)
    {
        var item = SelectedPattern(combo);
        preview.Background = item.PreviewBrush ?? Visuals.ToBrush("#FFFFFF");
        button.IsEnabled = ElementIds.IsValid(item.Id);
    }

    private void UpdateAllPatternControls()
    {
        UpdatePatternControls(CmbSurfacePattern, SurfaceFgPatternPreview, BtnSurfaceFgColor);
        UpdatePatternControls(CmbSurfaceBgPattern, SurfaceBgPatternPreview, BtnSurfaceBgColor);
        UpdatePatternControls(CmbCutPattern, CutFgPatternPreview, BtnCutFgColor);
        UpdatePatternControls(CmbCutBgPattern, CutBgPatternPreview, BtnCutBgColor);
        PatternInfo.Text =
            $"Superficie FG: {SelectedPattern(CmbSurfacePattern).Name} | Superficie BG: {SelectedPattern(CmbSurfaceBgPattern).Name} | " +
            $"Corte FG: {SelectedPattern(CmbCutPattern).Name} | Corte BG: {SelectedPattern(CmbCutBgPattern).Name}";
    }

    /// <summary>Selects the item in the combo, or "none" when this combo does not list it (cut patterns are drafting only).</summary>
    private void SelectPattern(ComboBox combo, PatternItem? item)
    {
        var list = (IEnumerable<PatternItem>)combo.ItemsSource;
        combo.SelectedItem = item is not null && list.Contains(item) ? item : _none;
    }

    private PatternItem? SolidPattern() => _patterns.FirstOrDefault(p => p.IsSolidFill) ?? _patterns.FirstOrDefault(p => p.Name == "<Solid Fill>");

    // ------------------------------------------------------------ display updates

    private void UpdateColorDisplay()
    {
        var hex = Visuals.Hex(_r, _g, _b);
        var brush = Visuals.ToBrush(hex);
        ColorSwatch.Background = brush;
        ColorPreview.Background = brush;
        ColorRGB.Text = $"{_r}, {_g}, {_b}";
        ColorHex.Text = hex;
    }

    private void UpdateRenderColorDisplay()
    {
        RenderColorPreview.Background = Visuals.ToBrush(Visuals.Hex((int)(_app.DiffuseR * 255), (int)(_app.DiffuseG * 255), (int)(_app.DiffuseB * 255)));
        RenderColorRGB.Text = string.Create(CultureInfo.InvariantCulture, $"{_app.DiffuseR:0.00}, {_app.DiffuseG:0.00}, {_app.DiffuseB:0.00}");
    }

    private void UpdateRoughnessPreview(double value)
    {
        var dark = (int)(220 - 120 * value);
        var light = (int)(245 - 80 * value);
        PreviewRoughness.Background = Visuals.Gradient(Visuals.Hex(light, light, light), Visuals.Hex(dark, dark, dark));
    }

    private void UpdateReflectivityPreview(double value) =>
        PreviewReflectivity.Background = Visuals.Gradient("#EAEAEA", Visuals.Hex(180, 210, (int)(190 + 65 * value)));

    private void UpdateGlossinessPreview(double value)
    {
        var high = (int)(210 + 45 * value);
        var low = (int)(190 - 50 * value);
        PreviewGlossiness.Background = Visuals.Gradient(Visuals.Hex(low, low, low), Visuals.Hex(high, high, high));
    }

    private void UpdateTransparencyPreview(double value)
    {
        var mix = (int)(255 - 110 * value);
        PreviewTransparency.Background = Visuals.Checker("#F2F2F2", Visuals.Hex(mix, mix, 255));
    }

    private static void SetPreviewEnabled(System.Windows.Controls.Border preview, bool enabled)
    {
        preview.Opacity = enabled ? 1.0 : 0.35;
        preview.BorderBrush = Visuals.ToBrush(enabled ? "#CCCCCC" : "#DDDDDD");
    }

    private void ConfigureAppearanceUi()
    {
        var caps = SchemaCapabilities.For(_app.Schema, _app.HasTexture);
        var parts = new List<string>();
        if (caps.Color) parts.Add("cor");
        if (caps.Roughness) parts.Add("rugosidade");
        if (caps.Reflectivity) parts.Add("refletividade");
        if (caps.Glossiness) parts.Add("brilho");
        if (caps.Transparency) parts.Add("transparencia");
        if (caps.Texture) parts.Add("textura");

        GrpRenderColor.Header = caps.ColorLabel;
        GrpRenderProps.Header = caps.PropsLabel;
        GrpTexturePreview.Header = caps.TextureLabel;
        SchemaCapsInfo.Text = $"Recursos editaveis neste schema: {(parts.Count > 0 ? string.Join(", ", parts) : "nenhum")}.";
        BtnPickRenderColor.IsEnabled = caps.Color;
        BtnChangeTexture.IsEnabled = caps.Texture;
        SliderRoughness.IsEnabled = caps.Roughness;
        SliderReflectivity.IsEnabled = caps.Reflectivity;
        SliderGlossiness.IsEnabled = caps.Glossiness;
        SliderRenderTransp.IsEnabled = caps.Transparency;

        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        RowRoughness.Visibility = Show(caps.Roughness);
        RowReflectivity.Visibility = Show(caps.Reflectivity);
        RowGlossiness.Visibility = Show(caps.Glossiness);
        RowRenderTransp.Visibility = Show(caps.Transparency);
        GrpRenderProps.Visibility = Show(caps.Roughness || caps.Reflectivity || caps.Glossiness || caps.Transparency);
        GrpTexturePreview.Visibility = Show(caps.Texture);
        GrpTextureEditor.Visibility = Visibility.Visible;
        SetPreviewEnabled(PreviewRoughness, caps.Roughness);
        SetPreviewEnabled(PreviewReflectivity, caps.Reflectivity);
        SetPreviewEnabled(PreviewGlossiness, caps.Glossiness);
        SetPreviewEnabled(PreviewTransparency, caps.Transparency);

        var schema = _app.Schema.Length > 0 ? _app.Schema : "N/A";
        TextureInfo.Text = _app.HasTexture ? $"Schema: {schema} | textura conectada controla a aparencia principal."
            : !caps.Color ? $"Schema: {schema} | este material nao usa cor manual nesta propriedade."
            : $"Schema: {schema} | cor editavel.";
    }

    private void RefreshFavoritesUi()
    {
        try
        {
            var db = Favorites.LoadDb();
            LstFavorites.ItemsSource = Favorites.List(db);
            var count = ((System.Text.Json.Nodes.JsonArray)db["favorites"]!).Count;
            var isFavorite = Favorites.IsFavorite(_material, db);
            FavoritesInfo.Text =
                "Biblioteca protegida por backup e escrita atomica. " +
                "Favoritos ficam numa biblioteca portatil: o material vai para qualquer projeto, sem abrir a origem. " +
                "Localizar usa a identidade original, nunca apenas o nome. " +
                "Snapshots antigos sao preservados, mas precisam ser favoritados novamente na origem para transferencia completa. " +
                $"Itens salvos: {count}. Arquivo: {StoragePaths.FavoritesFile}";
            BtnFavorite.Content = isFavorite ? "[Favorito]" : "Favoritar";
            BtnFavorite.Background = Visuals.ToBrush(isFavorite ? "#FFF1CC" : "#E1E1E1");
            BtnFavorite.BorderBrush = Visuals.ToBrush(isFavorite ? "#D2A23A" : "#CCCCCC");
            ColorSwatch.BorderBrush = Visuals.ToBrush(isFavorite ? "#D2A23A" : "#999999");
            FavoriteState.Text = isFavorite ? "Material favorito salvo" : "";
        }
        catch (Exception error)
        {
            FavoritesInfo.Text = $"Erro ao carregar favoritos: {error.Message}";
        }
    }

    private void UpdateTextureSlotsUi()
    {
        var slots = MaterialServices.GetTextureSlotAvailability(_doc, _material);
        string Path(TextureRole role) => _app.Textures.GetValueOrDefault(role) ?? "";
        bool Exists(TextureRole role) => Path(role).Length > 0 && File.Exists(TexturePaths.Resolve(Path(role)));
        string Label(TextureRole role) => Path(role).Length > 0 ? TexturePaths.FileName(Path(role)) : "Sem textura";

        TxtMainTexture.Text = Label(TextureRole.Main);
        TxtBumpTexture.Text = Label(TextureRole.Bump);
        TxtCutoutTexture.Text = Label(TextureRole.Cutout);
        TxtTransparencyTexture.Text = Label(TextureRole.Transparency);
        BtnSaveMainTexture.IsEnabled = Exists(TextureRole.Main);
        BtnSaveBumpTexture.IsEnabled = Exists(TextureRole.Bump);
        BtnSaveCutoutTexture.IsEnabled = Exists(TextureRole.Cutout);
        BtnSaveTransparencyTexture.IsEnabled = Exists(TextureRole.Transparency);
        BtnChangeMainTexture.IsEnabled = slots[TextureRole.Main];
        BtnChangeBumpTexture.IsEnabled = slots[TextureRole.Bump];
        BtnChangeCutoutTexture.IsEnabled = slots[TextureRole.Cutout];
        BtnChangeTransparencyTexture.IsEnabled = slots[TextureRole.Transparency];

        var detected = new[] { TextureRole.Main, TextureRole.Bump, TextureRole.Cutout, TextureRole.Transparency }.Count(r => Path(r).Length > 0);
        string YesNo(TextureRole role) => slots[role] ? "sim" : "nao";
        TextureSlotsInfo.Text = detected > 0 || slots.Values.Any(v => v)
            ? $"Texturas detectadas: {detected} | Slots editaveis: principal={YesNo(TextureRole.Main)}, bump={YesNo(TextureRole.Bump)}, " +
              $"recorte={YesNo(TextureRole.Cutout)}, transparencia={YesNo(TextureRole.Transparency)}"
            : "Nenhuma textura detectada automaticamente e nenhum slot editavel foi localizado neste material.";
    }

    // ------------------------------------------------------------ initial population

    private void Populate()
    {
        UpdateColorDisplay();
        MaterialName.Text = _material.Name;
        MaterialClass.Text = $"{_material.MaterialClass} | {_material.MaterialCategory}";
        ChkRenderAppearance.IsChecked = _material.UseRenderAppearanceForShading;
        BtnPickColor.IsEnabled = !_material.UseRenderAppearanceForShading;
        SliderTransparency.Value = _transparency;
        ValTransparency.Text = $"{_transparency}%";
        SliderShininess.Value = _shininess;
        ValShininess.Text = _shininess.ToString(CultureInfo.InvariantCulture);
        SliderSmoothness.Value = _smoothness;
        ValSmoothness.Text = _smoothness.ToString(CultureInfo.InvariantCulture);

        CmbSurfacePattern.ItemsSource = _patterns;
        CmbSurfaceBgPattern.ItemsSource = _patterns;
        CmbCutPattern.ItemsSource = _cutPatterns;
        CmbCutBgPattern.ItemsSource = _cutPatterns;
        foreach (var item in _patterns)
        {
            if (item.Id == _material.SurfaceForegroundPatternId) CmbSurfacePattern.SelectedItem = item;
            if (item.Id == _material.SurfaceBackgroundPatternId) CmbSurfaceBgPattern.SelectedItem = item;
        }
        foreach (var item in _cutPatterns)
        {
            if (item.Id == _material.CutForegroundPatternId) CmbCutPattern.SelectedItem = item;
            if (item.Id == _material.CutBackgroundPatternId) CmbCutBgPattern.SelectedItem = item;
        }
        UpdateAllPatternControls();
        SetSwatch(SurfaceFgColorSwatch, _surfaceFg);
        SetSwatch(SurfaceBgColorSwatch, _surfaceBg);
        SetSwatch(CutFgColorSwatch, _cutFg);
        SetSwatch(CutBgColorSwatch, _cutBg);

        var (schema, props) = MaterialServices.ReadAppearanceAsset(_doc, _material);
        SchemaName.Text = schema.Length > 0 ? schema : "N/A";
        AppearanceProps.ItemsSource = props.Count > 0
            ? props
            : [new PropItem("Info", "Sem propriedades extras relevantes para este schema.")];

        UpdateRenderColorDisplay();
        SliderRoughness.Value = _app.Roughness * 100;
        ValRoughness.Text = Pct(_app.Roughness * 100);
        UpdateRoughnessPreview(_app.Roughness);
        SliderReflectivity.Value = _app.Reflectivity * 100;
        ValReflectivity.Text = Pct(_app.Reflectivity * 100);
        UpdateReflectivityPreview(_app.Reflectivity);
        SliderGlossiness.Value = _app.Glossiness * 100;
        ValGlossiness.Text = Pct(_app.Glossiness * 100);
        UpdateGlossinessPreview(_app.Glossiness);
        SliderRenderTransp.Value = _app.Transparency * 100;
        ValRenderTransp.Text = Pct(_app.Transparency * 100);
        UpdateTransparencyPreview(_app.Transparency);

        if (_app.HasTexture)
        {
            var brush = Visuals.TextureBrush(_app.MainTexturePath);
            TexturePreview.Background = brush ?? Visuals.ToBrush("#F0F0F0");
            TexturePreviewInfo.Text = brush is not null
                ? TexturePaths.FileName(_app.MainTexturePath)
                : $"Nao foi possivel gerar preview da textura: {TexturePaths.Resolve(_app.MainTexturePath)}";
        }
        else
        {
            TexturePreview.Background = Visuals.ToBrush("#F0F0F0");
            TexturePreviewInfo.Text = "Sem textura conectada.";
        }
        UpdateTextureSlotsUi();
        ConfigureAppearanceUi();
        RefreshFavoritesUi();

        TxtName.Text = _material.Name;
        TxtClass.Text = _material.MaterialClass;
        TxtCategory.Text = _material.MaterialCategory;
        foreach (var (parameter, box) in new[]
        {
            (BuiltInParameter.ALL_MODEL_DESCRIPTION, TxtDescription), (BuiltInParameter.ALL_MODEL_MANUFACTURER, TxtManufacturer),
            (BuiltInParameter.ALL_MODEL_MODEL, TxtModel), (BuiltInParameter.ALL_MODEL_URL, TxtURL), (BuiltInParameter.ALL_MODEL_MARK, TxtMark),
        })
        {
            var text = _material.get_Parameter(parameter)?.AsString();
            if (!string.IsNullOrEmpty(text)) box.Text = text;
        }
        var comments = MaterialServices.CommentsParameter(_material)?.AsString();
        if (!string.IsNullOrEmpty(comments)) TxtComments.Text = comments;
    }

    // ------------------------------------------------------------ events

    private void Wire()
    {
        SliderTransparency.ValueChanged += (_, _) => { _transparency = (int)SliderTransparency.Value; ValTransparency.Text = $"{_transparency}%"; };
        SliderShininess.ValueChanged += (_, _) => { _shininess = (int)SliderShininess.Value; ValShininess.Text = _shininess.ToString(CultureInfo.InvariantCulture); };
        SliderSmoothness.ValueChanged += (_, _) => { _smoothness = (int)SliderSmoothness.Value; ValSmoothness.Text = _smoothness.ToString(CultureInfo.InvariantCulture); };
        SliderRoughness.ValueChanged += (_, _) => { _app.Roughness = SliderRoughness.Value / 100.0; ValRoughness.Text = Pct(SliderRoughness.Value); UpdateRoughnessPreview(_app.Roughness); };
        SliderReflectivity.ValueChanged += (_, _) => { _app.Reflectivity = SliderReflectivity.Value / 100.0; ValReflectivity.Text = Pct(SliderReflectivity.Value); UpdateReflectivityPreview(_app.Reflectivity); };
        SliderGlossiness.ValueChanged += (_, _) => { _app.Glossiness = SliderGlossiness.Value / 100.0; ValGlossiness.Text = Pct(SliderGlossiness.Value); UpdateGlossinessPreview(_app.Glossiness); };
        SliderRenderTransp.ValueChanged += (_, _) => { _app.Transparency = SliderRenderTransp.Value / 100.0; ValRenderTransp.Text = Pct(SliderRenderTransp.Value); UpdateTransparencyPreview(_app.Transparency); };
        ChkRenderAppearance.Click += (_, _) => BtnPickColor.IsEnabled = ChkRenderAppearance.IsChecked != true;

        foreach (var combo in new[] { CmbSurfacePattern, CmbSurfaceBgPattern, CmbCutPattern, CmbCutBgPattern })
            combo.SelectionChanged += (_, _) => UpdateAllPatternControls();

        BtnSurfaceSolid.Click += (_, _) => { SelectPattern(CmbSurfacePattern, SolidPattern()); UpdateAllPatternControls(); };
        BtnSurfaceClearBg.Click += (_, _) => { SelectPattern(CmbSurfaceBgPattern, _none); UpdateAllPatternControls(); };
        BtnCutSolid.Click += (_, _) => { SelectPattern(CmbCutPattern, SolidPattern()); UpdateAllPatternControls(); };
        BtnCopySurfaceToCut.Click += (_, _) => OnCopySurfaceToCut();

        BtnPickColor.Click += (_, _) => PickColor(hex => { (_r, _g, _b) = hex; UpdateColorDisplay(); });
        BtnPickRenderColor.Click += (_, _) => PickColor(rgb =>
        {
            (_app.DiffuseR, _app.DiffuseG, _app.DiffuseB) = (rgb.R / 255.0, rgb.G / 255.0, rgb.B / 255.0);
            UpdateRenderColorDisplay();
        });
        BtnSurfaceFgColor.Click += (_, _) => PickPatternColor(c => _surfaceFg = c, SurfaceFgColorSwatch);
        BtnSurfaceBgColor.Click += (_, _) => PickPatternColor(c => _surfaceBg = c, SurfaceBgColorSwatch);
        BtnCutFgColor.Click += (_, _) => PickPatternColor(c => _cutFg = c, CutFgColorSwatch);
        BtnCutBgColor.Click += (_, _) => PickPatternColor(c => _cutBg = c, CutBgColorSwatch);

        BtnChangeTexture.Click += (_, _) => Run(() => ChangeTexture(TextureRole.Main, "Textura principal alterada!"));
        TexturePreview.MouseLeftButtonUp += (_, _) => Run(() => OpenTextureEditor(TextureRole.Main));
        BtnChangeMainTexture.Click += (_, _) => Run(() => ChangeTexture(TextureRole.Main, "Textura principal alterada!"));
        BtnChangeBumpTexture.Click += (_, _) => Run(() => ChangeTexture(TextureRole.Bump, "Textura bump alterada!"));
        BtnChangeCutoutTexture.Click += (_, _) => Run(() => ChangeTexture(TextureRole.Cutout, "Textura de recorte alterada!"));
        BtnChangeTransparencyTexture.Click += (_, _) => Run(() => ChangeTexture(TextureRole.Transparency, "Textura de transparencia alterada!"));
        BtnSaveMainTexture.Click += (_, _) => SaveTexture(TextureRole.Main);
        BtnSaveBumpTexture.Click += (_, _) => SaveTexture(TextureRole.Bump);
        BtnSaveCutoutTexture.Click += (_, _) => SaveTexture(TextureRole.Cutout);
        BtnSaveTransparencyTexture.Click += (_, _) => SaveTexture(TextureRole.Transparency);

        BtnFavorite.Click += (_, _) => Run(OnToggleFavorite);
        BtnPaint.Click += (_, _) => { _request.Paint = (_material, _material.Name, "Modo Paint ativo. Clique nas faces e pressione ESC para sair."); Close(); };
        BtnApply.Click += (_, _) => Run(OnApply);
        BtnNewMaterial.Click += (_, _) => Run(OnNewMaterial);
        BtnRefreshFavorites.Click += (_, _) => Run(RefreshFavoritesUi);
        BtnRemoveFavorite.Click += (_, _) => Run(OnRemoveFavorite);
        BtnImportFavorite.Click += (_, _) => Run(OnImportFavorite);
        BtnPaintFavorite.Click += (_, _) => Run(OnPaintFavorite);
        BtnClose.Click += (_, _) => Close();
        BtnEditor.Click += (_, _) => Run(OnEditor);
    }

    private void OnCopySurfaceToCut()
    {
        SelectPattern(CmbCutPattern, CmbSurfacePattern.SelectedItem as PatternItem);
        SelectPattern(CmbCutBgPattern, CmbSurfaceBgPattern.SelectedItem as PatternItem);
        _cutFg = _surfaceFg;
        _cutBg = _surfaceBg;
        SetSwatch(CutFgColorSwatch, _cutFg);
        SetSwatch(CutBgColorSwatch, _cutBg);
        UpdateAllPatternControls();
    }

    private static (int R, int G, int B)? AskRgb()
    {
        var hex = Dialogs.AskColor();
        return hex is null ? null : Visuals.ParseHex(hex);
    }

    private static void PickColor(Action<(int R, int G, int B)> apply)
    {
        if (AskRgb() is { } rgb) apply(rgb);
    }

    private void PickPatternColor(Action<RevitColor> set, System.Windows.Controls.Border swatch)
    {
        if (AskRgb() is not { } rgb) return;
        var color = new RevitColor((byte)rgb.R, (byte)rgb.G, (byte)rgb.B);
        set(color);
        SetSwatch(swatch, color);
    }

    // ------------------------------------------------------------ textures

    private void SaveTexture(TextureRole role)
    {
        var source = TexturePaths.Resolve(_app.Textures.GetValueOrDefault(role));
        if (source.Length == 0 || !File.Exists(source))
        {
            Dialogs.Alert("Nao ha textura salva neste slot.", "Texturas");
            return;
        }
        var target = Dialogs.SaveFile("Salvar textura", Path.GetFileName(source));
        if (target is null) return;
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Dialogs.Alert($"Nao foi possivel salvar a textura.\n\n{error.Message}", "Texturas");
            return;
        }
        Notifications.Toast($"Textura salva em {target}", "Texturas");
    }

    private void ChangeTexture(TextureRole role, string successMessage)
    {
        var path = Dialogs.PickFile(TextureExtensions, "Selecionar Textura");
        if (path is null) return;
        Failures.Pop();
        if (MaterialServices.SetTextureSlot(_doc, _material, role, path))
        {
            _app.Textures[role] = path;
            if (role == TextureRole.Main)
            {
                TexturePreview.Background = Visuals.TextureBrush(path) ?? Visuals.ToBrush("#F0F0F0");
                TexturePreviewInfo.Text = TexturePaths.FileName(path);
            }
            UpdateTextureSlotsUi();
            ConfigureAppearanceUi();
            Notifications.Toast(successMessage, "Sucesso");
            return;
        }
        var reasons = Failures.Pop();
        Dialogs.Alert("Nao foi possivel trocar a textura deste slot.\n\n" +
                      (reasons.Count > 0 ? string.Join("\n", reasons) : "Verifique se o arquivo existe."), "Texturas");
    }

    private void OpenTextureEditor(TextureRole role)
    {
        var editor = new TextureEditorWindow(_doc, _material, role);
        editor.ShowDialog();
        if (!editor.Changed) return;
        _request.NextMaterial = _doc.GetElement(_material.Id) as Material;
        Close();
    }

    // ------------------------------------------------------------ apply

    private bool ApplyAllSteps()
    {
        var graphics = new MaterialGraphics
        {
            R = _r, G = _g, B = _b, Transparency = _transparency, Shininess = _shininess, Smoothness = _smoothness,
            UseRenderAppearance = ChkRenderAppearance.IsChecked == true,
            SurfaceForegroundPatternId = SelectedPattern(CmbSurfacePattern).Id,
            SurfaceBackgroundPatternId = SelectedPattern(CmbSurfaceBgPattern).Id,
            CutForegroundPatternId = SelectedPattern(CmbCutPattern).Id,
            CutBackgroundPatternId = SelectedPattern(CmbCutBgPattern).Id,
            SurfaceForegroundColor = _surfaceFg, SurfaceBackgroundColor = _surfaceBg, CutForegroundColor = _cutFg, CutBackgroundColor = _cutBg,
        };
        var identity = new MaterialIdentity
        {
            Name = TxtName.Text, Description = TxtDescription.Text, Manufacturer = TxtManufacturer.Text, Model = TxtModel.Text,
            Url = TxtURL.Text, MaterialClass = TxtClass.Text, Category = TxtCategory.Text, Mark = TxtMark.Text, Comments = TxtComments.Text,
        };
        // Every step runs even if an earlier one failed, so the failure message lists all the reasons.
        var graphicsOk = MaterialServices.ApplyMaterialChanges(_doc, _material.Id, graphics);
        var appearanceOk = MaterialServices.ApplyAppearanceChanges(_doc, _material.AppearanceAssetId,
            _app.DiffuseR, _app.DiffuseG, _app.DiffuseB, _app.Roughness, _app.Reflectivity, _app.Glossiness, _app.Transparency);
        var identityOk = MaterialServices.ApplyIdentityChanges(_doc, _material.Id, identity);
        return graphicsOk && appearanceOk && identityOk;
    }

    /// <summary>Graphics, appearance and identity in one transaction group: one Ctrl+Z, all or nothing.</summary>
    private void OnApply()
    {
        Failures.Pop();
        using var group = new TransactionGroup(_doc, "Editar material");
        group.Start();
        bool applied;
        List<string> reasons;
        try
        {
            applied = ApplyAllSteps();
            reasons = Failures.Pop().ToList();
        }
        catch (Exception error)
        {
            applied = false;
            Failures.Pop();
            reasons = [error.Message];
        }
        if (!applied)
        {
            group.RollBack();
            Dialogs.Alert("Nenhuma alteracao foi aplicada.\n\n" +
                          (reasons.Count > 0 ? string.Join("\n", reasons) : "Motivo nao informado pelo Revit."), "Editar material");
            return;
        }
        group.Assimilate();
        Dialogs.Alert("Material atualizado com sucesso!", "Sucesso");
    }

    // ------------------------------------------------------------ material and favorites

    private void OnNewMaterial()
    {
        var name = Dialogs.AskString("Nome do novo material", $"{_material.Name}_copy", "Novo Material");
        if (string.IsNullOrEmpty(name)) return;
        Failures.Pop();
        var created = MaterialServices.DuplicateMaterial(_doc, _material, name);
        if (created is null)
        {
            Dialogs.Alert("Nao foi possivel criar o novo material.\n\n" + string.Join("\n", Failures.Pop()), "Erro");
            return;
        }
        Notifications.Toast($"Material criado: {created.Name}", "Sucesso");
        _request.NextMaterial = created;
        Close();
    }

    private void OnToggleFavorite()
    {
        try
        {
            if (Favorites.IsFavorite(_material))
            {
                if (!Dialogs.Alert("Remover este favorito da biblioteca? Um backup sera preservado.", "Favoritos", ask: true)) return;
                var warnings = Favorites.Remove(RevitContext.Application, _material, null);
                Notifications.Toast("Material removido dos favoritos.", "Favoritos");
                if (warnings.Count > 0) Dialogs.Alert(string.Join("\n", warnings), "Favoritos");
            }
            else
            {
                var item = Favorites.AddOrUpdate(RevitContext.Application, _doc, _material);
                Notifications.Toast("Material salvo nos favoritos.", "Favoritos");
                if (item.Warnings.Count > 0) Dialogs.Alert(string.Join("\n", item.Warnings), "Favoritos");
            }
            RefreshFavoritesUi();
        }
        catch (Exception error)
        {
            Dialogs.Alert($"Erro ao alterar favorito: {error.Message}", "Favoritos");
        }
    }

    private void OnRemoveFavorite()
    {
        if (LstFavorites.SelectedItem is not FavoriteItem item)
        {
            Dialogs.Alert("Selecione um favorito na lista.", "Favoritos");
            return;
        }
        if (!Dialogs.Alert("Remover este favorito da biblioteca? Um backup sera preservado.", "Favoritos", ask: true)) return;
        try
        {
            var warnings = Favorites.Remove(RevitContext.Application, null, item.Id);
            if (warnings.Count > 0) Dialogs.Alert(string.Join("\n", warnings), "Favoritos");
        }
        catch (Exception error)
        {
            Dialogs.Alert(error.Message, "Favoritos");
            return;
        }
        RefreshFavoritesUi();
        Notifications.Toast("Favorito removido.", "Favoritos");
    }

    private Material? FavoriteMaterial(string cancelTitle)
    {
        if (LstFavorites.SelectedItem is not FavoriteItem item)
        {
            Dialogs.Alert("Selecione um favorito na lista.", "Favoritos");
            return null;
        }
        try
        {
            var result = Favorites.GetOrCreateMaterial(RevitContext.Application, _doc, item.Data);
            if (result.MissingTextures.Count > 0)
                Notifications.Toast($"Material transferido, mas {result.MissingTextures.Count} textura(s) nao foram encontradas neste computador.", "Favoritos");
            return result.Material;
        }
        catch (Exception error)
        {
            Dialogs.Alert(error.Message, cancelTitle);
            return null;
        }
    }

    private void OnImportFavorite()
    {
        var material = FavoriteMaterial("Favoritos - transferencia cancelada");
        if (material is null) return;
        Notifications.Toast($"Material localizado no projeto: {material.Name}", "Favoritos");
        _request.NextMaterial = material;
        Close();
    }

    private void OnPaintFavorite()
    {
        var material = FavoriteMaterial("Favoritos - transferencia cancelada");
        if (material is null) return;
        var label = (LstFavorites.SelectedItem as FavoriteItem)?.Name ?? material.Name;
        _request.Paint = (material, label, $"Pintando com favorito: {material.Name}. Clique nas faces e pressione ESC para sair.");
        Close();
    }

    private void OnEditor()
    {
        _uidoc.Selection.SetElementIds(new List<ElementId> { _material.Id });
        _request.OpenNative = true;
        Close();
    }
}
