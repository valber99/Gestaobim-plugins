using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Gestaobim.Ai;
using Gestaobim.Ai.Revit;
using Gestaobim.Render.Core;
using Gestaobim.Render.Revit;

namespace Gestaobim.Render.UI;

/// <summary>
/// Janela do GB Render: controles a esquerda, preview da imagem a direita, cancelamento seguro ao fechar,
/// geracao de camadas (sem IA), render com IA e edicao localizada de objeto.
/// </summary>
public static class RenderWindow
{
    public const string Title = "GB Render";

    public static void Show(ExtractedScene scene, string projectName)
    {
        var settings = AiSettings.Load();
        using var windowCts = new CancellationTokenSource();
        var closed = false;

        // ---- Coluna esquerda: controles em cards
        var leftStack = new StackPanel { Margin = new Thickness(16, 14, 10, 14) };

        foreach (var warning in scene.Warnings)
        {
            leftStack.Children.Add(new Border
            {
                Background = AiUi.WarnBg,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock { Text = warning, FontSize = 11.5, Foreground = AiUi.WarnFg, TextWrapping = TextWrapping.Wrap },
            });
        }

        // Card 1: Configuracao do render
        var cfgPanel = new StackPanel();
        var topRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var qualLbl = new TextBlock { Text = "Qualidade:", VerticalAlignment = VerticalAlignment.Center, Foreground = AiUi.TitleFg, Margin = new Thickness(0, 0, 8, 0) };
        var quality = new ComboBox { SelectedIndex = 1, Width = 170, Padding = new Thickness(6, 4, 6, 4) };
        quality.Items.Add("Rascunho (1K)");
        quality.Items.Add("Padrão (2K)");
        quality.Items.Add("Alta (4K)");
        var ratioText = new TextBlock
        {
            Text = $"Proporção: {RenderJob.SupportedAspectRatio(scene.Camera.Aspect)}",
            Foreground = AiUi.MutedFg,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(qualLbl, Dock.Left);
        DockPanel.SetDock(quality, Dock.Left);
        topRow.Children.Add(qualLbl);
        topRow.Children.Add(quality);
        topRow.Children.Add(ratioText);
        cfgPanel.Children.Add(topRow);

        cfgPanel.Children.Add(new TextBlock { Text = "Pedido do render", Foreground = AiUi.TitleFg, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 3) });
        var prompt = new TextBox
        {
            Text = "Render fotorrealista editorial, luz natural rasante.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 58,
            Padding = new Thickness(6),
            BorderBrush = AiUi.SecondaryBorder,
        };
        cfgPanel.Children.Add(prompt);

        cfgPanel.Children.Add(new TextBlock { Text = "Fundo (céu e entorno)", Foreground = AiUi.TitleFg, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 3) });
        var environment = new TextBox
        {
            Text = "",
            Padding = new Thickness(6, 4, 6, 4),
            BorderBrush = AiUi.SecondaryBorder,
            ToolTip = "Ex.: fim de tarde tropical, vegetação costeira difusa, luz quente 3800K",
        };
        cfgPanel.Children.Add(environment);

        var replaceBackground = new CheckBox { Content = "Trocar o céu e o entorno visto pelas janelas (o resto da cena não muda)", IsChecked = true, Margin = new Thickness(0, 7, 0, 0), Foreground = AiUi.TitleFg };
        var strip = new CheckBox { Content = "Não enviar fabricante e modelo das famílias", IsChecked = settings.StripManufacturers, Margin = new Thickness(0, 4, 0, 0), Foreground = AiUi.TitleFg };
        cfgPanel.Children.Add(replaceBackground);
        cfgPanel.Children.Add(strip);

        cfgPanel.Children.Add(new TextBlock { Text = "Pasta de saída", Foreground = AiUi.TitleFg, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 3) });
        var folder = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GestaoBIM", "Render", Sanitize($"{projectName}-{scene.ViewName}-{DateTime.Now:yyyyMMdd-HHmm}")),
            Padding = new Thickness(6, 4, 6, 4),
            BorderBrush = AiUi.SecondaryBorder,
        };
        var browse = AiUi.SecondaryButton("Procurar…");
        browse.Margin = new Thickness(6, 0, 0, 0);
        var folderRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folder);
        cfgPanel.Children.Add(folderRow);

        var renderButton = AiUi.PrimaryButton("Gerar render com IA", isDefault: true);
        renderButton.Margin = new Thickness(0, 0, 8, 0);
        var layersButton = AiUi.SecondaryButton("Gerar camadas (sem IA)");
        layersButton.Margin = new Thickness(0, 0, 8, 0);
        var configButton = AiUi.SecondaryButton("Configurar IA…");
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(renderButton);
        actions.Children.Add(layersButton);
        actions.Children.Add(configButton);
        cfgPanel.Children.Add(actions);

        var (statusBox, statusText, statusBar) = AiUi.StatusBanner();
        cfgPanel.Children.Add(statusBox);

        leftStack.Children.Add(AiUi.Card("Configuração do render", cfgPanel));

        // Card 2: Edicao localizada
        var editPanel = new StackPanel();
        var objects = new ComboBox { IsEnabled = false, Padding = new Thickness(6, 4, 6, 4) };
        var instruction = new TextBox
        {
            Padding = new Thickness(6, 4, 6, 4),
            BorderBrush = AiUi.SecondaryBorder,
            IsEnabled = false,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "Ex.: trocar o revestimento por madeira ripada clara",
        };
        var editButton = AiUi.SecondaryButton("Editar objeto selecionado");
        editButton.IsEnabled = false;
        editButton.HorizontalAlignment = HorizontalAlignment.Left;
        editButton.Margin = new Thickness(0, 8, 0, 0);
        editPanel.Children.Add(objects);
        editPanel.Children.Add(instruction);
        editPanel.Children.Add(editButton);
        editPanel.Children.Add(AiUi.Note("Disponível após o primeiro render. Só a região do objeto (máscara Color-ID) pode mudar; o plugin confere isso."));
        leftStack.Children.Add(AiUi.Card("Editar um objeto do render", editPanel, new Thickness(0)));

        // ---- Coluna direita: Preview
        var previewImage = new Image { Stretch = Stretch.Uniform };
        var previewPlaceholder = new TextBlock
        {
            Text = "A pré-visualização do render\n(ou da camada clay)\naparecerá aqui.",
            Foreground = AiUi.MutedFg,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 13,
        };
        var previewHost = new Grid { MinHeight = 360, Background = AiUi.WindowBg };
        previewHost.Children.Add(previewPlaceholder);
        previewHost.Children.Add(previewImage);

        var openImage = AiUi.SecondaryButton("Abrir imagem");
        openImage.IsEnabled = false;
        openImage.Margin = new Thickness(0, 0, 8, 0);
        var openFolder = AiUi.SecondaryButton("Abrir pasta");
        var previewBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        previewBtns.Children.Add(openImage);
        previewBtns.Children.Add(openFolder);

        var previewStack = new DockPanel();
        DockPanel.SetDock(previewBtns, Dock.Bottom);
        previewStack.Children.Add(previewBtns);
        previewStack.Children.Add(new Border
        {
            BorderBrush = AiUi.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = previewHost,
        });
        var rightCard = AiUi.Card("Pré-visualização", previewStack, new Thickness(6, 14, 16, 14));

        // Grid 2 colunas
        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star), MinWidth = 430 });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        var leftScroll = new ScrollViewer { Content = leftStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(rightCard, 1);
        bodyGrid.Children.Add(leftScroll);
        bodyGrid.Children.Add(rightCard);

        // Rodape
        var close = AiUi.SecondaryButton("Fechar", isCancel: true);
        close.Width = 96;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new Border
        {
            Background = Brushes.White,
            BorderBrush = AiUi.CardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10, 16, 10),
            Child = close,
        };

        var subtitle = $"Vista: {scene.ViewName}  ·  {scene.Scene.TriangleCount:N0} triângulos de {scene.Scene.Elements.Count:N0} elementos" +
                       (scene.SkippedTransparent > 0 ? $"  ·  {scene.SkippedTransparent:N0} triângulos de vidro transparentes" : "");
        var header = AiUi.Header("GB Render — Render com IA", subtitle);

        var mainDock = new DockPanel { Background = AiUi.WindowBg };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        mainDock.Children.Add(header);
        mainDock.Children.Add(footer);
        mainDock.Children.Add(bodyGrid);

        var window = new Window
        {
            Title = "GB Render — Render com IA",
            Content = mainDock,
            Width = 980,
            Height = 760,
            MinWidth = 780,
            MinHeight = 580,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Background = AiUi.WindowBg,
        };
        Dialogs.Attach(window);

        window.Closing += (_, _) =>
        {
            closed = true;
            try { windowCts.Cancel(); } catch { }
        };

        browse.Click += (_, _) =>
        {
            if (Dialogs.PickFolder(Path.GetDirectoryName(folder.Text), "Pasta onde ficam as camadas e os renders") is { } chosen) folder.Text = chosen;
        };

        configButton.Click += (_, _) =>
        {
            AiSettingsWindow.ShowDialog();
            settings = AiSettings.Load();
        };

        RenderPasses? passes = null;
        RenderQuality? passesQuality = null;
        GeneratedRender? lastRender = null;
        string? lastModel = null;
        string? previewPath = null;
        var busy = false;

        RenderQuality SelectedQuality() => quality.SelectedIndex switch { 0 => RenderQuality.OneK, 2 => RenderQuality.FourK, _ => RenderQuality.TwoK };

        ManifestOptions Options() => new()
        {
            UserPrompt = prompt.Text.Trim(),
            TargetEnvironment = environment.Text.Trim(),
            BackgroundMode = replaceBackground.IsChecked == true ? "replace_exterior_only" : "keep_background",
            StripManufacturers = strip.IsChecked == true,
        };

        void SetBusy(bool value)
        {
            busy = value;
            layersButton.IsEnabled = renderButton.IsEnabled = configButton.IsEnabled = !value;
            editButton.IsEnabled = !value && lastRender is not null && objects.Items.Count > 0;
        }

        void ShowPreviewBytes(byte[] bytes, string? filePath)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                previewImage.Source = bmp;
                previewPlaceholder.Visibility = Visibility.Collapsed;
                previewPath = filePath;
                openImage.IsEnabled = !string.IsNullOrEmpty(filePath) && File.Exists(filePath);
            }
            catch { }
        }

        async Task<RenderPasses> EnsurePasses()
        {
            var q = SelectedQuality();
            var options = Options();
            AiUi.SetStatus(statusBox, statusText, statusBar, "Calculando camadas alinhadas (clay, color-ID, profundidade e fundo)…", StatusKind.Info, busy: true);
            var built = await Task.Run(() => RenderJob.BuildPasses(scene.Scene, scene.Camera, q, scene.Meta, options), windowCts.Token);
            passes = built;
            passesQuality = q;
            objects.Items.Clear();
            foreach (var o in built.Manifest.Manifest["scene_objects"]!.AsArray().OfType<JsonObject>().OrderByDescending(o => o["coverage"]!.GetValue<double>()))
                objects.Items.Add(new ComboBoxItem { Content = $"{o["family_type"] ?? o["category"]}  ({o["coverage"]!.GetValue<double>():P1} da imagem)", Tag = o });
            if (objects.Items.Count > 0) objects.SelectedIndex = 0;
            return built;
        }

        layersButton.Click += async (_, _) =>
        {
            if (busy || closed) return;
            SetBusy(true);
            try
            {
                var built = await EnsurePasses();
                var outDir = folder.Text.Trim();
                var zip = await Task.Run(() => RenderJob.WritePasses(built, outDir), windowCts.Token);
                if (closed) return;
                ShowPreviewBytes(Png.Encode(built.Layers.Clay), Path.Combine(outDir, RenderBundle.ClayName));
                AiUi.SetStatus(statusBox, statusText, statusBar,
                    $"Camadas gravadas e conferidas ({built.Width}×{built.Height}, {built.Manifest.ObjectCount} objetos). Pacote: {zip}",
                    StatusKind.Ok);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!closed) AiUi.SetStatus(statusBox, statusText, statusBar, error.Message, StatusKind.Error);
            }
            finally
            {
                if (!closed) SetBusy(false);
            }
        };

        renderButton.Click += async (_, _) =>
        {
            if (busy || closed) return;
            if (!settings.PrivacyAcknowledged)
            {
                if (MessageBox.Show(window, AiSettingsWindow.PrivacyNote + "\n\nContinuar?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                settings.PrivacyAcknowledged = true;
                settings.Save();
            }
            SetBusy(true);
            try
            {
                var built = await EnsurePasses();
                if (closed) return;
                ShowPreviewBytes(Png.Encode(built.Layers.Clay), null);
                AiUi.SetStatus(statusBox, statusText, statusBar, "Conectando ao gateway de IA…", StatusKind.Info, busy: true);
                using var connection = await AiService.ConnectAsync(windowCts.Token);
                if (closed) return;
                var model = connection.ImageModel;
                if (model.Length == 0) throw new AiException("Nenhum modelo de imagem disponível. Clique em \"Configurar IA…\" e conecte uma conta ou chave de API.");
                AiUi.SetStatus(statusBox, statusText, statusBar, $"Gerando render com {model} ({RenderJob.SupportedAspectRatio((double)built.Width / built.Height)})… pode levar alguns minutos.", StatusKind.Info, busy: true);
                var render = await RenderJob.GenerateAsync(connection.Client, model, built, SelectedQuality(), folder.Text.Trim(), cancel: windowCts.Token);
                if (closed) return;
                lastRender = render;
                lastModel = model;
                ShowPreviewBytes(render.Bytes, render.ImagePath);
                AiUi.SetStatus(statusBox, statusText, statusBar, $"Render pronto em {render.Report.DurationSeconds:0} s: {render.ImagePath}", StatusKind.Ok);
                objects.IsEnabled = instruction.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                if (!closed) AiUi.SetStatus(statusBox, statusText, statusBar, "O modelo de imagem demorou demais para responder. Tente de novo ou use uma qualidade menor.", StatusKind.Error);
            }
            catch (Exception error)
            {
                if (!closed) AiUi.SetStatus(statusBox, statusText, statusBar, error.Message, StatusKind.Error);
            }
            finally
            {
                if (!closed) SetBusy(false);
            }
        };

        editButton.Click += async (_, _) =>
        {
            if (busy || closed || passes is null || lastRender is null || objects.SelectedItem is not ComboBoxItem { Tag: JsonObject chosen }) return;
            if (string.IsNullOrWhiteSpace(instruction.Text))
            {
                AiUi.SetStatus(statusBox, statusText, statusBar, "Diga o que mudar no objeto selecionado.", StatusKind.Warn);
                return;
            }
            SetBusy(true);
            try
            {
                var ids = chosen["element_ids"] is JsonArray many
                    ? many.Select(n => n!.GetValue<long>()).ToList()
                    : [long.Parse(chosen["id"]!.ToString().Replace("elem_", ""))];
                var description = chosen["family_type"]?.ToString() ?? chosen["category"]!.ToString();
                AiUi.SetStatus(statusBox, statusText, statusBar, $"Editando {description}…", StatusKind.Info, busy: true);
                using var connection = await AiService.ConnectAsync(windowCts.Token);
                if (closed) return;
                var edited = await RenderJob.EditAsync(connection.Client, lastModel ?? connection.ImageModel, passes, lastRender.Bytes, ids, instruction.Text.Trim(), description,
                    passesQuality ?? RenderQuality.TwoK, folder.Text.Trim(), WpfDecoder.Decode, cancel: windowCts.Token);
                if (closed) return;
                lastRender = edited.Render;
                ShowPreviewBytes(edited.Render.Bytes, edited.Render.ImagePath);
                var kind = edited.Check is { Ok: true } ? StatusKind.Ok : StatusKind.Warn;
                AiUi.SetStatus(statusBox, statusText, statusBar,
                    $"Edição pronta: {edited.Render.ImagePath}" + (edited.Check is { } c ? $" · mudou {c.ChangedInside:P0} do objeto e {c.ChangedOutside:P1} do resto." : ""),
                    kind);
            }
            catch (OperationCanceledException)
            {
                if (!closed) AiUi.SetStatus(statusBox, statusText, statusBar, "O modelo de imagem demorou demais para responder. Tente de novo.", StatusKind.Error);
            }
            catch (Exception error)
            {
                if (!closed) AiUi.SetStatus(statusBox, statusText, statusBar, error.Message, StatusKind.Error);
            }
            finally
            {
                if (!closed) SetBusy(false);
            }
        };

        openImage.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(previewPath) && File.Exists(previewPath))
                    Process.Start(new ProcessStartInfo(previewPath) { UseShellExecute = true });
            }
            catch (Exception error) { AiUi.SetStatus(statusBox, statusText, statusBar, error.Message, StatusKind.Error); }
        };

        openFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(folder.Text.Trim());
                Process.Start(new ProcessStartInfo(folder.Text.Trim()) { UseShellExecute = true });
            }
            catch (Exception error) { AiUi.SetStatus(statusBox, statusText, statusBar, error.Message, StatusKind.Error); }
        };

        window.ShowDialog();
    }

    private static string Sanitize(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

/// <summary>Decodifica PNG, JPEG ou WebP com o WPF para pixels RGB (a conferencia da edicao aceita qualquer formato que o modelo devolver).</summary>
internal static class WpfDecoder
{
    public static RawImage? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            var bgra = new byte[width * height * 4];
            frame.CopyPixels(bgra, width * 4, 0);
            var rgb = new byte[width * height * 3];
            for (var i = 0; i < width * height; i++)
            {
                rgb[i * 3] = bgra[i * 4 + 2];
                rgb[i * 3 + 1] = bgra[i * 4 + 1];
                rgb[i * 3 + 2] = bgra[i * 4];
            }
            return new RawImage(width, height, PngKind.Rgb8, rgb);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
