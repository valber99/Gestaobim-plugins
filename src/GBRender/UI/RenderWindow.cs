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
/// A janela do render: qualidade, pedido, ambiente e pasta; "Gerar camadas" (sem IA, pacote conferido), "Gerar render" (camadas + modelo de
/// imagem) e, depois do primeiro render, a edicao localizada de um objeto pela mascara. Montada em codigo.
/// </summary>
public static class RenderWindow
{
    public const string Title = "GB Render";

    public static void Show(ExtractedScene scene, string projectName)
    {
        var settings = AiSettings.Load();
        var root = new StackPanel { Margin = new Thickness(16) };
        var hint = Brushes.Gray;

        void Heading(string text) => root.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        void Note(string text) => root.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        root.Children.Add(new TextBlock
        {
            Text = $"Vista: {scene.ViewName}. {scene.Scene.TriangleCount:N0} triângulos de {scene.Scene.Elements.Count:N0} elementos" +
                   (scene.SkippedTransparent > 0 ? $"; {scene.SkippedTransparent:N0} triângulos de vidro ficam transparentes." : "."),
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var warning in scene.Warnings)
            root.Children.Add(new TextBlock { Text = warning, FontSize = 11, Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap });

        Heading("Qualidade");
        var quality = new ComboBox { SelectedIndex = 1, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        quality.Items.Add("Rascunho (1K)");
        quality.Items.Add("Padrão (2K)");
        quality.Items.Add("Alta (4K)");
        root.Children.Add(quality);

        Heading("Pedido do render");
        var prompt = new TextBox { Text = "Render fotorrealista editorial, luz natural rasante.", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 54, Padding = new Thickness(4) };
        root.Children.Add(prompt);

        Heading("Fundo (céu e entorno)");
        var environment = new TextBox { Text = "", Padding = new Thickness(4), ToolTip = "Ex.: fim de tarde tropical, vegetação costeira difusa, luz quente 3800K" };
        root.Children.Add(environment);
        var replaceBackground = new CheckBox { Content = "Trocar o céu e o entorno visto pelas janelas (o resto da cena não muda)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(replaceBackground);

        var strip = new CheckBox { Content = "Não enviar fabricante e modelo das famílias", IsChecked = settings.StripManufacturers, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(strip);

        Heading("Pasta de saída");
        var folder = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GestaoBIM", "Render", Sanitize($"{projectName}-{scene.ViewName}-{DateTime.Now:yyyyMMdd-HHmm}")),
            Padding = new Thickness(4),
        };
        var browse = new Button { Content = "Procurar…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        browse.Click += (_, _) =>
        {
            if (Dialogs.PickFolder(Path.GetDirectoryName(folder.Text), "Pasta onde ficam as camadas e os renders") is { } chosen) folder.Text = chosen;
        };
        var folderRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folder);
        root.Children.Add(folderRow);

        var layersButton = new Button { Content = "Gerar camadas (sem IA)", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var renderButton = new Button { Content = "Gerar render com IA", Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
        var openFolder = new Button { Content = "Abrir pasta", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(layersButton);
        actions.Children.Add(renderButton);
        actions.Children.Add(openFolder);
        root.Children.Add(actions);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(status);

        // ---- edicao localizada
        Heading("Editar um objeto do render");
        var objects = new ComboBox { IsEnabled = false };
        var instruction = new TextBox { Padding = new Thickness(4), IsEnabled = false, Margin = new Thickness(0, 4, 0, 0), ToolTip = "Ex.: trocar o couro por tecido azul" };
        var editButton = new Button { Content = "Editar objeto", IsEnabled = false, Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(objects);
        root.Children.Add(instruction);
        root.Children.Add(editButton);
        Note("Depois do primeiro render, escolha um objeto e diga o que mudar. Só a região dele (a máscara Color-ID) pode mudar; o programa confere isso.");

        var close = new Button { Content = "Fechar", IsCancel = true, Width = 90, Padding = new Thickness(0, 5, 0, 5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        root.Children.Add(close);

        var window = new Window
        {
            Title = "GB Render — Render com IA", Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Width = 640, Height = 780, ResizeMode = ResizeMode.CanResizeWithGrip, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
        };
        Dialogs.Attach(window);

        RenderPasses? passes = null;
        RenderQuality? passesQuality = null;
        GeneratedRender? lastRender = null;
        string? lastModel = null;
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
            layersButton.IsEnabled = renderButton.IsEnabled = !value;
            editButton.IsEnabled = !value && lastRender is not null && objects.Items.Count > 0;
        }

        async Task<RenderPasses> EnsurePasses()
        {
            var q = SelectedQuality();
            var options = Options();
            status.Text = "Calculando as camadas alinhadas (clay, color-ID, profundidade e fundo)…";
            var built = await Task.Run(() => RenderJob.BuildPasses(scene.Scene, scene.Camera, q, scene.Meta, options));
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
            if (busy) return;
            SetBusy(true);
            status.Foreground = Brushes.DarkSlateGray;
            try
            {
                var built = await EnsurePasses();
                var zip = await Task.Run(() => RenderJob.WritePasses(built, folder.Text.Trim()));
                status.Foreground = Brushes.DarkGreen;
                status.Text = $"Camadas gravadas e conferidas ({built.Width}×{built.Height}, {built.Manifest.ObjectCount} objetos, todas as cores da máscara estão no manifesto). Pacote: {zip}";
            }
            catch (Exception error)
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = error.Message;
            }
            finally
            {
                SetBusy(false);
            }
        };

        renderButton.Click += async (_, _) =>
        {
            if (busy) return;
            if (!settings.PrivacyAcknowledged)
            {
                if (!Dialogs.Alert(AiSettingsWindow.PrivacyNote + "\n\nContinuar?", Title, ask: true)) return;
                settings.PrivacyAcknowledged = true;
                settings.Save();
            }
            SetBusy(true);
            status.Foreground = Brushes.DarkSlateGray;
            try
            {
                var built = await EnsurePasses();
                status.Text = "Conectando ao gateway de IA…";
                using var connection = await AiService.ConnectAsync();
                var model = connection.ImageModel;
                if (model.Length == 0) throw new AiException("Nenhum modelo de imagem disponível. Abra \"Configurar IA\" e conecte uma conta ou uma chave de API.");
                status.Text = $"Enviando as camadas ao modelo {model} e esperando o render… (pode levar alguns minutos)";
                var render = await RenderJob.GenerateAsync(connection.Client, model, built, SelectedQuality(), folder.Text.Trim());
                lastRender = render;
                lastModel = model;
                status.Foreground = Brushes.DarkGreen;
                status.Text = $"Render pronto em {render.Report.DurationSeconds:0} s: {render.ImagePath}";
                Process.Start(new ProcessStartInfo(render.ImagePath) { UseShellExecute = true });
                objects.IsEnabled = instruction.IsEnabled = true;
            }
            catch (Exception error)
            {
                // Handler assincrono: uma excecao que escapasse daqui (tempo esgotado, rede, abrir a imagem) derrubaria o Revit.
                status.Foreground = Brushes.Firebrick;
                status.Text = error is TaskCanceledException ? "O modelo de imagem demorou demais para responder. Tente de novo ou use uma qualidade menor." : error.Message;
            }
            finally
            {
                SetBusy(false);
            }
        };

        editButton.Click += async (_, _) =>
        {
            if (busy || passes is null || lastRender is null || objects.SelectedItem is not ComboBoxItem { Tag: JsonObject chosen }) return;
            if (string.IsNullOrWhiteSpace(instruction.Text))
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = "Diga o que mudar no objeto.";
                return;
            }
            SetBusy(true);
            status.Foreground = Brushes.DarkSlateGray;
            try
            {
                var ids = chosen["element_ids"] is JsonArray many
                    ? many.Select(n => n!.GetValue<long>()).ToList()
                    : [long.Parse(chosen["id"]!.ToString().Replace("elem_", ""))];
                var description = chosen["family_type"]?.ToString() ?? chosen["category"]!.ToString();
                status.Text = $"Editando {description}…";
                using var connection = await AiService.ConnectAsync();
                var edited = await RenderJob.EditAsync(connection.Client, lastModel ?? connection.ImageModel, passes, lastRender.Bytes, ids, instruction.Text.Trim(), description,
                    passesQuality ?? RenderQuality.TwoK, folder.Text.Trim(), WpfDecoder.Decode);
                lastRender = edited.Render;
                status.Foreground = edited.Check is { Ok: true } ? Brushes.DarkGreen : Brushes.DarkOrange;
                status.Text = $"Edição pronta: {edited.Render.ImagePath}" +
                              (edited.Check is { } c ? $". Mudou {c.ChangedInside:P0} do objeto e {c.ChangedOutside:P1} do resto da imagem." : "");
                Process.Start(new ProcessStartInfo(edited.Render.ImagePath) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                // Handler assincrono: uma excecao que escapasse daqui (tempo esgotado, rede, abrir a imagem) derrubaria o Revit.
                status.Foreground = Brushes.Firebrick;
                status.Text = error is TaskCanceledException ? "O modelo de imagem demorou demais para responder. Tente de novo ou use uma qualidade menor." : error.Message;
            }
            finally
            {
                SetBusy(false);
            }
        };

        openFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(folder.Text.Trim());
                Process.Start(new ProcessStartInfo(folder.Text.Trim()) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = error.Message;
            }
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
