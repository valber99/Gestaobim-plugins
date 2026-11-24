using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Gestaobim.Ai.Revit;

/// <summary>
/// "Configurar IA": contas de assinatura (OAuth), chaves de API, modelo de cada tarefa e privacidade.
/// Carrega os modelos automaticamente ao abrir, destaca modelos de imagem e cancela tarefas ao fechar.
/// </summary>
public static class AiSettingsWindow
{
    private const string Title = "Gestão BIM — Configurar IA";

    public const string OAuthRisk =
        "Conectar uma ASSINATURA (Claude, ChatGPT, Antigravity) usa o login dela fora do aplicativo oficial. Os termos dessas assinaturas, em geral, " +
        "limitam o uso aos produtos do próprio fornecedor, e a conta pode ser bloqueada. Quem prefere não arriscar pode usar uma chave de API.\n\n" +
        "Continuar e conectar a conta?";

    public const string PrivacyNote =
        "Os planos de cota enviam o resumo da vista (nomes de famílias e posições) e o render envia as imagens do projeto e o manifesto " +
        "(famílias, fabricantes e materiais) ao provedor escolhido.";

    public static void ShowDialog()
    {
        var settings = AiSettings.Load();
        using var windowCts = new CancellationTokenSource();
        var closed = false;
        var busy = false;

        var content = new StackPanel { Margin = new Thickness(16) };

        // ---- 1. Contas por assinatura (OAuth)
        var oauthPanel = new StackPanel();
        var accounts = new ListBox
        {
            Height = 72,
            BorderBrush = AiUi.SecondaryBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
        };
        void RefreshAccounts()
        {
            accounts.Items.Clear();
            foreach (var account in OAuthLogin.ConnectedAccounts()) accounts.Items.Add(account);
            if (accounts.Items.Count == 0) accounts.Items.Add("(nenhuma conta conectada)");
        }
        RefreshAccounts();
        oauthPanel.Children.Add(accounts);

        var loginRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var loginButtons = new List<Button>();
        var disconnect = AiUi.SecondaryButton("Desconectar selecionada");
        disconnect.Margin = new Thickness(0, 0, 8, 4);

        var test = AiUi.SecondaryButton("Testar conexão e atualizar modelos");
        var save = AiUi.PrimaryButton("Salvar", isDefault: true);
        save.Width = 100;
        save.Margin = new Thickness(0, 0, 8, 0);
        var close = AiUi.SecondaryButton("Fechar", isCancel: true);
        close.Width = 90;

        var (statusBox, statusText, statusBar) = AiUi.StatusBanner();
        var planBox = new ComboBox { IsEditable = true, Text = settings.PlanModel, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 2, 0, 8) };
        var imageBox = new ComboBox { IsEditable = true, Text = settings.ImageModel, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 2, 0, 4) };
        var imageModelWarn = new TextBlock
        {
            Foreground = AiUi.WarnFg,
            Background = AiUi.WarnBg,
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
        };

        void UpdateImageWarning()
        {
            var chosen = imageBox.Text.Trim();
            if (chosen.Length > 0 && !AiUi.LooksLikeImageModel(chosen))
            {
                imageModelWarn.Text = $"Atenção: \"{chosen}\" parece ser um modelo de texto, não de imagem. Deixe em branco (automático) ou escolha um modelo com \"image\" no nome.";
                imageModelWarn.Visibility = Visibility.Visible;
            }
            else
            {
                imageModelWarn.Visibility = Visibility.Collapsed;
            }
        }
        imageBox.SelectionChanged += (_, _) => UpdateImageWarning();
        imageBox.LostFocus += (_, _) => UpdateImageWarning();
        imageBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateImageWarning()));
        UpdateImageWarning();

        void SetBusy(bool value)
        {
            busy = value;
            foreach (var b in loginButtons) b.IsEnabled = !value;
            disconnect.IsEnabled = !value;
            test.IsEnabled = !value;
            save.IsEnabled = !value;
        }

        void PopulateModels(IReadOnlyList<string> models)
        {
            var prevPlan = planBox.Text;
            var prevImg = imageBox.Text;
            planBox.Items.Clear();
            imageBox.Items.Clear();
            var orderedImg = models.OrderByDescending(AiUi.LooksLikeImageModel).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var m in models) planBox.Items.Add(m);
            foreach (var m in orderedImg) imageBox.Items.Add(m);
            planBox.Text = prevPlan;
            imageBox.Text = prevImg;
            UpdateImageWarning();
        }

        foreach (var provider in OAuthLogin.Providers)
        {
            var button = AiUi.SecondaryButton("Conectar " + provider.ShortName);
            button.Margin = new Thickness(0, 0, 8, 4);
            var captured = provider;
            button.Click += async (_, _) =>
            {
                if (busy || closed) return;
                if (!settings.OAuthRiskAcknowledged)
                {
                    if (!Dialogs.Alert(OAuthRisk, Title, ask: true)) return;
                    settings.OAuthRiskAcknowledged = true;
                    settings.Save();
                }
                SetBusy(true);
                AiUi.SetStatus(statusBox, statusText, statusBar, $"Abrindo o navegador para entrar em {captured.Name}. Conclua o login lá (até 5 min)…", StatusKind.Info, busy: true);
                try
                {
                    var (added, _, output) = await OAuthLogin.LoginAsync(captured, TimeSpan.FromMinutes(5), windowCts.Token);
                    if (closed) return;
                    RefreshAccounts();
                    if (added)
                    {
                        AiUi.SetStatus(statusBox, statusText, statusBar, "Conta conectada! Reiniciando o gateway e carregando modelos…", StatusKind.Info, busy: true);
                        var endpoint = await GatewayHost.RestartAsync(settings.ProviderKeys(), windowCts.Token);
                        using var client = new AiClient(endpoint);
                        var models = await client.ListModelsAsync(windowCts.Token);
                        if (closed) return;
                        PopulateModels(models);
                        AiUi.SetStatus(statusBox, statusText, statusBar, $"Conta conectada com sucesso ({models.Count} modelos disponíveis).", StatusKind.Ok);
                    }
                    else
                    {
                        AiUi.SetStatus(statusBox, statusText, statusBar,
                            "Nenhuma conta nova apareceu." + (string.IsNullOrWhiteSpace(output) ? "" : " " + Redactor.Shorten(output.Trim(), 300)),
                            StatusKind.Warn);
                    }
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
            loginButtons.Add(button);
            loginRow.Children.Add(button);
        }

        disconnect.Click += async (_, _) =>
        {
            if (busy || closed) return;
            if (accounts.SelectedItem is not string name || name.StartsWith('(')) return;
            if (!Dialogs.Alert($"Desconectar \"{name}\"? Para usar de novo será preciso entrar outra vez.", Title, ask: true)) return;
            SetBusy(true);
            try
            {
                OAuthLogin.Disconnect(name);
                RefreshAccounts();
                AiUi.SetStatus(statusBox, statusText, statusBar, $"\"{name}\" desconectada. Reiniciando o gateway…", StatusKind.Info, busy: true);
                await GatewayHost.RestartAsync(settings.ProviderKeys(), windowCts.Token);
                if (!closed) AiUi.SetStatus(statusBox, statusText, statusBar, $"\"{name}\" desconectada.", StatusKind.Ok);
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
        loginRow.Children.Add(disconnect);
        oauthPanel.Children.Add(loginRow);
        oauthPanel.Children.Add(AiUi.Note("O login abre o navegador padrão e o token fica salvo apenas na sua pasta de usuário (%LOCALAPPDATA%\\GestaoBIM\\gbai\\auth)."));
        content.Children.Add(AiUi.Card("Contas de assinatura (OAuth)", oauthPanel));

        // ---- 2. Chaves de API
        var keysPanel = new StackPanel();
        var keyBoxes = new Dictionary<string, PasswordBox>(StringComparer.OrdinalIgnoreCase);
        var keyBadges = new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, label) in new[] { ("gemini", "Google Gemini (AI Studio)"), ("claude", "Anthropic (Claude)"), ("codex", "OpenAI") })
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var name = new TextBlock { Text = label, Width = 185, VerticalAlignment = VerticalAlignment.Center, Foreground = AiUi.TitleFg };
            var configured = new TextBlock
            {
                Text = settings.HasKey(id) ? "● configurada" : "não configurada",
                Width = 105,
                Foreground = settings.HasKey(id) ? AiUi.OkFg : AiUi.MutedFg,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                FontSize = 12,
            };
            keyBadges[id] = configured;
            DockPanel.SetDock(name, Dock.Left);
            DockPanel.SetDock(configured, Dock.Right);
            var box = new PasswordBox { Padding = new Thickness(6, 4, 6, 4), BorderBrush = AiUi.SecondaryBorder, ToolTip = "Cole a chave nova aqui. Deixe em branco para manter; digite '-' para apagar." };
            keyBoxes[id] = box;
            row.Children.Add(name);
            row.Children.Add(configured);
            row.Children.Add(box);
            keysPanel.Children.Add(row);
        }
        keysPanel.Children.Add(AiUi.Note("Guardadas cifradas com DPAPI do Windows (só este usuário, neste computador). Em branco = manter; digite um traço (-) para apagar."));
        content.Children.Add(AiUi.Card("Chaves de API", keysPanel));

        // ---- 3. Modelos por tarefa
        var modelsPanel = new StackPanel();
        modelsPanel.Children.Add(new TextBlock { Text = "Planejar cotas (texto)", Foreground = AiUi.TitleFg, FontSize = 12.5 });
        modelsPanel.Children.Add(planBox);
        modelsPanel.Children.Add(new TextBlock { Text = "Gerar imagens (render)", Foreground = AiUi.TitleFg, FontSize = 12.5 });
        modelsPanel.Children.Add(imageBox);
        modelsPanel.Children.Add(imageModelWarn);
        modelsPanel.Children.Add(AiUi.Note("Deixe em branco para o plugin escolher automaticamente o melhor modelo disponível para cada tarefa."));
        content.Children.Add(AiUi.Card("Modelo de cada tarefa", modelsPanel));

        // ---- 4. Privacidade
        var privPanel = new StackPanel();
        var strip = new CheckBox { Content = "Não enviar fabricante e modelo das famílias no render", IsChecked = settings.StripManufacturers, Foreground = AiUi.TitleFg };
        privPanel.Children.Add(strip);
        privPanel.Children.Add(AiUi.Note(PrivacyNote));
        content.Children.Add(AiUi.Card("Privacidade", privPanel, new Thickness(0, 0, 0, 4)));

        content.Children.Add(statusBox);

        // ---- Rodape
        var footerDock = new DockPanel { Margin = new Thickness(16, 10, 16, 10) };
        DockPanel.SetDock(test, Dock.Left);
        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightBtns.Children.Add(save);
        rightBtns.Children.Add(close);
        DockPanel.SetDock(rightBtns, Dock.Right);
        footerDock.Children.Add(test);
        footerDock.Children.Add(rightBtns);

        var footer = new Border
        {
            Background = Brushes.White,
            BorderBrush = AiUi.CardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerDock,
        };

        var mainDock = new DockPanel { Background = AiUi.WindowBg };
        var header = AiUi.Header("Configurar IA", "Conecte contas ou chaves de API e escolha os modelos usados por GB Cotas e GB Render");
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        mainDock.Children.Add(header);
        mainDock.Children.Add(footer);
        mainDock.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var window = new Window
        {
            Title = Title,
            Content = mainDock,
            Width = 660,
            Height = 760,
            MinWidth = 580,
            MinHeight = 560,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Background = AiUi.WindowBg,
        };
        Dialogs.Attach(window);

        window.Closed += (_, _) =>
        {
            closed = true;
            try { windowCts.Cancel(); } catch { }
        };

        async Task RefreshModelsAsync(bool restartGateway)
        {
            if (busy || closed) return;
            SetBusy(true);
            AiUi.SetStatus(statusBox, statusText, statusBar, restartGateway ? "Reiniciando o gateway e testando conexão…" : "Carregando modelos disponíveis…", StatusKind.Info, busy: true);
            try
            {
                if (restartGateway)
                {
                    ApplyKeys(settings, keyBoxes, keyBadges);
                    settings.Save();
                }
                var endpoint = restartGateway
                    ? await GatewayHost.RestartAsync(settings.ProviderKeys(), windowCts.Token)
                    : await GatewayHost.EnsureStartedAsync(settings.ProviderKeys(), windowCts.Token);
                using var client = new AiClient(endpoint);
                var health = await GatewayHost.CheckHealthAsync(endpoint, windowCts.Token);
                var models = await client.ListModelsAsync(windowCts.Token);
                if (closed) return;
                PopulateModels(models);
                var imgCount = models.Count(AiUi.LooksLikeImageModel);
                if (models.Count > 0)
                {
                    AiUi.SetStatus(statusBox, statusText, statusBar,
                        $"Gateway ativo em 127.0.0.1:{health.Port} — {models.Count} modelos disponíveis ({imgCount} de imagem).",
                        StatusKind.Ok);
                }
                else
                {
                    AiUi.SetStatus(statusBox, statusText, statusBar,
                        "Gateway ativo, mas sem modelos: conecte uma conta OAuth ou informe uma chave de API.",
                        StatusKind.Warn);
                }
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
        }

        test.Click += async (_, _) => await RefreshModelsAsync(restartGateway: true);

        // Auto-carrega modelos ao abrir se ja houver conta ou chave configurada
        window.Loaded += async (_, _) =>
        {
            if (OAuthLogin.ConnectedAccounts().Count > 0 || settings.ProviderKeys().Count > 0)
                await RefreshModelsAsync(restartGateway: false);
        };

        save.Click += async (_, _) =>
        {
            if (busy || closed) return;
            SetBusy(true);
            try
            {
                var changedKeys = ApplyKeys(settings, keyBoxes, keyBadges);
                settings.PlanModel = planBox.Text.Trim();
                settings.ImageModel = imageBox.Text.Trim();
                settings.StripManufacturers = strip.IsChecked == true;
                settings.PrivacyAcknowledged = true;
                settings.Save();
                if (changedKeys)
                {
                    AiUi.SetStatus(statusBox, statusText, statusBar, "Aplicando chaves no gateway…", StatusKind.Info, busy: true);
                    try { await GatewayHost.RestartAsync(settings.ProviderKeys(), windowCts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception error)
                    {
                        if (!closed) MessageBox.Show(window, "As configurações foram salvas, mas o gateway não reiniciou:\n" + error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                if (!closed && window.IsVisible) window.DialogResult = true;
            }
            catch (Exception error)
            {
                if (!closed) AiUi.SetStatus(statusBox, statusText, statusBar, "Não foi possível salvar: " + error.Message, StatusKind.Error);
            }
            finally
            {
                if (!closed) SetBusy(false);
            }
        };

        window.ShowDialog();
    }

    private static bool ApplyKeys(AiSettings settings, Dictionary<string, PasswordBox> boxes, Dictionary<string, TextBlock> badges)
    {
        var changed = false;
        foreach (var (provider, box) in boxes)
        {
            var typed = box.Password.Trim();
            if (typed.Length == 0) continue;
            if (typed.IndexOfAny(['\r', '\n']) >= 0)
                throw new InvalidOperationException($"A chave de \"{provider}\" contém quebras de linha inválidas.");
            if (typed == "-") settings.Keys.Remove(provider);
            else settings.Keys[provider] = typed;
            box.Clear();
            badges[provider].Text = settings.HasKey(provider) ? "● configurada" : "não configurada";
            badges[provider].Foreground = settings.HasKey(provider) ? AiUi.OkFg : AiUi.MutedFg;
            changed = true;
        }
        return changed;
    }
}
