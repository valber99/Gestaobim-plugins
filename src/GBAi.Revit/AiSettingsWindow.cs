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
/// "Configurar IA": contas de assinatura (OAuth), chaves de API, modelo de cada tarefa e privacidade. Montada em codigo, como as
/// outras janelas. As chaves nunca voltam para a tela: so se sabe se estao configuradas.
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
        var hint = Brushes.Gray;
        var root = new StackPanel { Margin = new Thickness(16) };

        void Heading(string text) => root.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 4) });
        void Note(string text) => root.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        // ---- contas por assinatura
        Heading("Contas de assinatura (OAuth)");
        var accounts = new ListBox { Height = 70 };
        void RefreshAccounts()
        {
            accounts.Items.Clear();
            foreach (var account in OAuthLogin.ConnectedAccounts()) accounts.Items.Add(account);
            if (accounts.Items.Count == 0) accounts.Items.Add("(nenhuma conta conectada)");
        }
        RefreshAccounts();
        root.Children.Add(accounts);

        var loginRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var loginButtons = new List<Button>();
        foreach (var provider in OAuthLogin.Providers)
        {
            var button = new Button { Content = "Conectar " + provider.ShortName, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 4) };
            var captured = provider;
            button.Click += async (_, _) =>
            {
                if (!settings.OAuthRiskAcknowledged)
                {
                    if (!Dialogs.Alert(OAuthRisk, Title, ask: true)) return;
                    settings.OAuthRiskAcknowledged = true;
                    settings.Save();
                }
                foreach (var b in loginButtons) b.IsEnabled = false;
                status.Foreground = Brushes.DarkSlateGray;
                status.Text = $"Abrindo o navegador para entrar em {captured.Name}. Termine o login lá (até 5 minutos)…";
                try
                {
                    var (added, _, output) = await OAuthLogin.LoginAsync(captured, TimeSpan.FromMinutes(5));
                    RefreshAccounts();
                    status.Foreground = added ? Brushes.DarkGreen : Brushes.Firebrick;
                    status.Text = added
                        ? "Conta conectada. Clique em \"Testar conexão\" para ver os modelos."
                        : "Nenhuma conta nova apareceu." + (string.IsNullOrWhiteSpace(output) ? "" : " " + Redactor.Shorten(output.Trim(), 300));
                    if (added) await GatewayHost.RestartAsync(settings.ProviderKeys());
                }
                catch (Exception error)
                {
                    status.Foreground = Brushes.Firebrick;
                    status.Text = error.Message;
                }
                finally
                {
                    foreach (var b in loginButtons) b.IsEnabled = true;
                }
            };
            loginButtons.Add(button);
            loginRow.Children.Add(button);
        }
        var disconnect = new Button { Content = "Desconectar a conta selecionada", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 4) };
        disconnect.Click += async (_, _) =>
        {
            if (accounts.SelectedItem is not string name || name.StartsWith('(')) return;
            if (!Dialogs.Alert($"Desconectar \"{name}\"? Para usar de novo será preciso entrar outra vez.", Title, ask: true)) return;
            // Handler assincrono: nada pode escapar daqui (derrubaria o Revit).
            try
            {
                OAuthLogin.Disconnect(name);
                RefreshAccounts();
                status.Foreground = Brushes.DarkSlateGray;
                status.Text = $"\"{name}\" desconectada. Reiniciando o gateway…";
                await GatewayHost.RestartAsync(settings.ProviderKeys());
                status.Text = $"\"{name}\" desconectada.";
            }
            catch (Exception error)
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = error.Message;
            }
        };
        loginRow.Children.Add(disconnect);
        root.Children.Add(loginRow);
        Note("O login abre o navegador padrão e o token fica numa pasta só sua (%LOCALAPPDATA%\\GestaoBIM\\gbai\\auth).");

        // ---- chaves de API
        Heading("Chaves de API");
        var keyBoxes = new Dictionary<string, PasswordBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, label) in new[] { ("gemini", "Google Gemini (AI Studio)"), ("claude", "Anthropic (Claude)"), ("codex", "OpenAI") })
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var name = new TextBlock { Text = label, Width = 180, VerticalAlignment = VerticalAlignment.Center };
            var configured = new TextBlock { Text = settings.HasKey(id) ? "configurada" : "não configurada", Width = 100, Foreground = settings.HasKey(id) ? Brushes.DarkGreen : hint, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            DockPanel.SetDock(name, Dock.Left);
            DockPanel.SetDock(configured, Dock.Right);
            var box = new PasswordBox { Padding = new Thickness(4), ToolTip = "Cole a chave nova aqui. Deixe em branco para manter a atual." };
            keyBoxes[id] = box;
            row.Children.Add(name);
            row.Children.Add(configured);
            row.Children.Add(box);
            root.Children.Add(row);
        }
        Note("Guardadas cifradas com a DPAPI do Windows (só este usuário, neste computador). Em branco = manter; digite um traço (-) para apagar.");

        // ---- modelos
        Heading("Modelo de cada tarefa");
        var planBox = new ComboBox { IsEditable = true, Text = settings.PlanModel, Margin = new Thickness(0, 0, 0, 4) };
        var imageBox = new ComboBox { IsEditable = true, Text = settings.ImageModel };
        var planRow = new DockPanel();
        DockPanel.SetDock(planRow, Dock.Top);
        root.Children.Add(new TextBlock { Text = "Planejar cotas (texto)", Margin = new Thickness(0, 0, 0, 2) });
        root.Children.Add(planBox);
        root.Children.Add(new TextBlock { Text = "Gerar imagens (render)", Margin = new Thickness(0, 4, 0, 2) });
        root.Children.Add(imageBox);
        Note("Em branco o plugin escolhe um modelo adequado da lista. \"Testar conexão\" carrega a lista que o gateway oferece.");

        // ---- privacidade
        Heading("Privacidade");
        var strip = new CheckBox { Content = "Não enviar fabricante e modelo das famílias no render", IsChecked = settings.StripManufacturers };
        root.Children.Add(strip);
        Note(PrivacyNote);

        // ---- teste
        var test = new Button { Content = "Testar conexão", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            status.Foreground = Brushes.DarkSlateGray;
            status.Text = "Iniciando o gateway local…";
            try
            {
                ApplyKeys(settings, keyBoxes);
                settings.Save();
                var endpoint = await GatewayHost.RestartAsync(settings.ProviderKeys());
                using var client = new AiClient(endpoint);
                var health = await GatewayHost.CheckHealthAsync(endpoint);
                var models = await client.ListModelsAsync();
                planBox.Items.Clear();
                imageBox.Items.Clear();
                foreach (var m in models) { planBox.Items.Add(m); imageBox.Items.Add(m); }
                status.Foreground = models.Count > 0 ? Brushes.DarkGreen : Brushes.DarkOrange;
                status.Text = models.Count > 0
                    ? $"Gateway ativo em 127.0.0.1:{health.Port} (sem token: {health.StatusWithoutToken}; com token: {health.StatusWithToken}). {models.Count} modelos disponíveis."
                    : "Gateway ativo, mas sem modelos: conecte uma conta ou informe uma chave de API.";
            }
            catch (Exception error)
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = error.Message;
            }
            finally
            {
                test.IsEnabled = true;
            }
        };
        root.Children.Add(test);
        root.Children.Add(status);

        var save = new Button { Content = "Salvar", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 5, 0, 5) };
        var close = new Button { Content = "Fechar", IsCancel = true, Width = 90, Padding = new Thickness(0, 5, 0, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        var window = new Window
        {
            Title = Title,
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Width = 640,
            Height = 760,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
        };
        Dialogs.Attach(window);

        save.Click += async (_, _) =>
        {
            // Handler assincrono: nada pode escapar daqui (derrubaria o Revit).
            try
            {
                var changedKeys = ApplyKeys(settings, keyBoxes);
                settings.PlanModel = planBox.Text.Trim();
                settings.ImageModel = imageBox.Text.Trim();
                settings.StripManufacturers = strip.IsChecked == true;
                settings.PrivacyAcknowledged = true;
                settings.Save();
                if (changedKeys)
                {
                    try { await GatewayHost.RestartAsync(settings.ProviderKeys()); }
                    catch (Exception error) { Dialogs.Alert("As configurações foram salvas, mas o gateway não reiniciou:\n" + error.Message, Title); }
                }
                if (window.IsVisible) window.DialogResult = true;
            }
            catch (Exception error)
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = "Não foi possível salvar: " + error.Message;
            }
        };
        window.ShowDialog();
    }

    /// <summary>Aplica o que foi digitado nas caixas de chave. Devolve true se alguma chave mudou.</summary>
    private static bool ApplyKeys(AiSettings settings, Dictionary<string, PasswordBox> boxes)
    {
        var changed = false;
        foreach (var (provider, box) in boxes)
        {
            var typed = box.Password.Trim();
            if (typed.Length == 0) continue;
            if (typed == "-") settings.Keys.Remove(provider);
            else settings.Keys[provider] = typed;
            box.Clear();
            changed = true;
        }
        return changed;
    }
}
