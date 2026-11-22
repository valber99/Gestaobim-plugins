using System;
using System.Windows;
using System.Windows.Controls;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Revit;

namespace Gestaobim.Familias.UI;

/// <summary>
/// As unicas configuracoes do usuario: a pasta das familias baixadas, o limite do cache e a conta. O resto e padrao
/// (catalogo e busca por IA instalados, familias vindas do servidor). Montada em codigo.
/// </summary>
public static class SettingsWindow
{
    /// <summary>Mostra a janela. Devolve true se o usuário salvou.</summary>
    public static bool Edit(Window? owner, string? intro)
    {
        var settings = FamiliasSettings.Load();
        var panel = new StackPanel { Margin = new Thickness(16) };
        var hintBrush = System.Windows.Media.Brushes.Gray;

        if (intro is not null)
            panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Foreground = System.Windows.Media.Brushes.DarkSlateGray });

        // ---- pasta das familias baixadas
        panel.Children.Add(new TextBlock { Text = "Pasta das famílias do Revit", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        var folder = new TextBox { Text = settings.DownloadsFolder, Padding = new Thickness(4) };
        var browse = new Button { Content = "Procurar…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4) };
        browse.Click += (_, _) =>
        {
            if (Dialogs.PickFolder(folder.Text, "Pasta onde ficam (ou vão ficar) as famílias do Revit") is { } chosen) folder.Text = chosen;
        };
        var folderRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folder);
        panel.Children.Add(folderRow);
        panel.Children.Add(new TextBlock
        {
            Text = "As famílias baixadas do servidor ficam aqui. Se você já tem famílias numa pasta (ou a biblioteca inteira), escolha essa pasta: "
                 + "as que já estão lá são reconhecidas e não são baixadas de novo. Pode ser local ou de rede.",
            FontSize = 11, Foreground = hintBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        // ---- cache
        panel.Children.Add(new TextBlock { Text = "Espaço usado pelas famílias baixadas", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        var unlimited = new RadioButton { Content = "Sem limite", IsChecked = settings.CacheLimitGb == 0, Margin = new Thickness(0, 0, 0, 4) };
        var limited = new RadioButton { Content = "Limitar a", IsChecked = settings.CacheLimitGb > 0, VerticalAlignment = VerticalAlignment.Center };
        var gb = new TextBox { Text = settings.CacheLimitGb > 0 ? settings.CacheLimitGb.ToString() : "10", Width = 60, Padding = new Thickness(4), Margin = new Thickness(6, 0, 6, 0), IsEnabled = settings.CacheLimitGb > 0 };
        limited.Checked += (_, _) => gb.IsEnabled = true;
        unlimited.Checked += (_, _) => gb.IsEnabled = false;
        var limitRow = new StackPanel { Orientation = Orientation.Horizontal };
        limitRow.Children.Add(limited);
        limitRow.Children.Add(gb);
        limitRow.Children.Add(new TextBlock { Text = "GB", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(unlimited);
        panel.Children.Add(limitRow);
        panel.Children.Add(new TextBlock
        {
            Text = "Com limite, as famílias baixadas menos usadas são apagadas (podem ser baixadas de novo). Famílias que já estavam na pasta e pastas de rede nunca são apagadas.",
            FontSize = 11, Foreground = hintBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });

        // ---- conta
        var session = LibraryService.GetSession();
        var account = new TextBlock { Margin = new Thickness(0, 14, 0, 4), FontWeight = FontWeights.SemiBold };
        var signIn = new Button { Content = "Entrar", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var signOut = new Button { Content = "Sair", Padding = new Thickness(14, 4, 14, 4) };
        void UpdateAccount()
        {
            account.Text = session.Auth.HasSession ? "Conta: conectada ao servidor." : "Conta: não conectada. Entre para baixar as famílias.";
            signIn.IsEnabled = !session.Auth.HasSession;
            signOut.IsEnabled = session.Auth.HasSession;
        }
        var accountButtons = new StackPanel { Orientation = Orientation.Horizontal };
        accountButtons.Children.Add(signIn);
        accountButtons.Children.Add(signOut);
        panel.Children.Add(account);
        panel.Children.Add(accountButtons);
        UpdateAccount();

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
            Title = "GB Famílias — Configurações", Content = panel, Width = 620, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF7, 0xF8, 0xFA)),
        };
        if (owner is { IsVisible: true }) window.Owner = owner; else Dialogs.Attach(window);

        // O login abre a janela de andamento por cima DESTA (que esta visivel).
        signIn.Click += (_, _) => { ServerFlows.SignIn(window); UpdateAccount(); };
        signOut.Click += (_, _) => { session.Auth.Logout(); UpdateAccount(); };

        save.Click += (_, _) =>
        {
            var limitGb = 0;
            if (limited.IsChecked == true && (!int.TryParse(gb.Text.Trim(), out limitGb) || limitGb < 1))
            {
                problems.Text = "O limite deve ser um número inteiro de GB (1 ou mais).";
                return;
            }
            settings.DownloadsFolder = folder.Text.Trim();
            settings.CacheLimitGb = limitGb;
            var found = settings.Problems();
            if (found.Count > 0)
            {
                problems.Text = string.Join("\n", found);
                return;
            }
            settings.Save();
            window.DialogResult = true;
        };

        return window.ShowDialog() == true;
    }
}
