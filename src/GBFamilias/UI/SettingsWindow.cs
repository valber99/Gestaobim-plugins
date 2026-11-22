using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Revit;

namespace Gestaobim.Familias.UI;

/// <summary>De onde vem a biblioteca, onde ficam as famílias baixadas, a conta e a busca por IA. Montada em código.</summary>
public static class SettingsWindow
{
    /// <summary>Mostra a janela. Devolve true se o usuário salvou.</summary>
    public static bool Edit(Window? owner, string? intro)
    {
        var settings = FamiliasSettings.Load();
        var panel = new StackPanel { Margin = new Thickness(16) };

        if (intro is not null)
            panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Foreground = System.Windows.Media.Brushes.DarkSlateGray });

        TextBox AddRow(Panel parent, string label, string value, string? hint, Func<string?, string?> pick)
        {
            parent.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
            var box = new TextBox { Text = value, Padding = new Thickness(4) };
            var button = new Button { Content = "Procurar…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (_, _) => { if (pick(box.Text) is { } chosen) box.Text = chosen; };
            var row = new DockPanel();
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            row.Children.Add(box);
            parent.Children.Add(row);
            if (hint is not null)
                parent.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            return box;
        }

        // ---- modo
        var server = new RadioButton { Content = "Servidor (recomendado): o catálogo fica no computador e cada família é baixada ao inserir", IsChecked = settings.Mode == LibraryMode.Server, Margin = new Thickness(0, 0, 0, 4) };
        var local = new RadioButton { Content = "Pasta local (avançado): banco e famílias já estão numa pasta ou numa rede", IsChecked = settings.Mode == LibraryMode.Local };
        panel.Children.Add(server);
        panel.Children.Add(local);

        // ---- servidor
        var serverPanel = new StackPanel();
        var downloads = AddRow(serverPanel, "Pasta das famílias baixadas (obrigatória para baixar)", settings.DownloadsFolder,
            "As famílias baixadas ficam aqui, na mesma estrutura de pastas da biblioteca. Pode ser local ou de rede (uma pasta de rede compartilhada serve a todos e nunca é limpa pelo plugin).",
            start => Dialogs.PickFolder(start, "Pasta onde as famílias baixadas serão guardadas"));
        serverPanel.Children.Add(new TextBlock { Text = "Limite do cache em GB (0 = sem limite)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
        var limit = new TextBox { Text = settings.CacheLimitGb.ToString(), Width = 80, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left };
        serverPanel.Children.Add(limit);

        var session = LibraryService.GetSession();
        var account = new TextBlock { Margin = new Thickness(0, 12, 0, 4) };
        var signIn = new Button { Content = "Entrar", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var signOut = new Button { Content = "Sair", Padding = new Thickness(14, 4, 14, 4) };
        void UpdateAccount()
        {
            account.Text = session.Auth.HasSession ? "Conta: conectado ao servidor." : "Conta: não conectado. Entre para baixar o catálogo e as famílias.";
            signIn.IsEnabled = !session.Auth.HasSession;
            signOut.IsEnabled = session.Auth.HasSession;
        }
        signIn.Click += (_, _) => { ServerFlows.SignIn(Application.Current?.MainWindow); UpdateAccount(); };
        signOut.Click += (_, _) => { session.Auth.Logout(); UpdateAccount(); };
        UpdateAccount();
        var accountButtons = new StackPanel { Orientation = Orientation.Horizontal };
        accountButtons.Children.Add(signIn);
        accountButtons.Children.Add(signOut);
        serverPanel.Children.Add(account);
        serverPanel.Children.Add(accountButtons);

        // ---- local
        var localPanel = new StackPanel();
        var db = AddRow(localPanel, "Banco da biblioteca (gbfam.sqlite)", settings.LibraryDb, null,
            start => Dialogs.PickFile("Banco SQLite|*.sqlite;*.db|Todos os arquivos|*.*", "Banco da biblioteca", Path.GetDirectoryName(start ?? "")));
        var root = AddRow(localPanel, "Pasta das famílias (.rfa)", settings.LibraryRoot,
            "Os caminhos do banco são relativos a esta pasta. Para uma biblioteca compartilhada, aponte para a pasta de rede.",
            start => Dialogs.PickFolder(start, "Pasta que contém as famílias da biblioteca"));

        panel.Children.Add(serverPanel);
        panel.Children.Add(localPanel);
        void ShowMode()
        {
            serverPanel.Visibility = server.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            localPanel.Visibility = server.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        }
        server.Checked += (_, _) => ShowMode();
        local.Checked += (_, _) => ShowMode();
        ShowMode();

        // ---- comum
        var exe = AddRow(panel, "gbfam.exe (busca por IA)", settings.GbfamExe,
            "Deixe em branco para procurar automaticamente (%APPDATA%\\GestaoBIM\\Tools\\gbfam).",
            start => Dialogs.PickFile("gbfam.exe|gbfam.exe|Programas|*.exe", "Motor de busca por IA", Path.GetDirectoryName(start ?? "")));

        var ai = new CheckBox { Content = "Ligar a busca por IA (entende o sentido da busca e aceita imagens)", IsChecked = settings.SemanticEnabled, Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(ai);

        var problems = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(problems);

        var save = new Button { Content = "Salvar", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 5, 0, 5) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Width = 90, Padding = new Thickness(0, 5, 0, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "GB Famílias — Configurações", Content = panel, Width = 680, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0xF8, 0xFA)),
        };
        if (owner is not null) window.Owner = owner; else Dialogs.Attach(window);

        save.Click += (_, _) =>
        {
            if (!int.TryParse(limit.Text.Trim(), out var limitGb) || limitGb < 0)
            {
                problems.Text = "O limite do cache deve ser um número inteiro de GB (0 = sem limite).";
                return;
            }
            var candidate = new FamiliasSettings
            {
                Mode = server.IsChecked == true ? LibraryMode.Server : LibraryMode.Local,
                LibraryDb = db.Text.Trim(), LibraryRoot = root.Text.Trim(),
                DownloadsFolder = downloads.Text.Trim(), CacheLimitGb = limitGb, SyncEveryHours = settings.SyncEveryHours,
                GbfamExe = exe.Text.Trim(), SemanticEnabled = ai.IsChecked == true,
            };
            var found = candidate.Problems();
            if (found.Count > 0)
            {
                problems.Text = string.Join("\n", found);
                return;
            }
            candidate.Save();
            window.DialogResult = true;
        };

        return window.ShowDialog() == true;
    }
}
