using System.Windows;
using System.Windows.Interop;
using Gestaobim.Familias.Revit;

namespace Gestaobim.Familias.UI;

/// <summary>Caixas de mensagem e seletores, presos ao Revit para nunca ficarem escondidos atras dele.</summary>
public static class Dialogs
{
    public const string Title = "GB Famílias";

    private sealed class Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    /// <summary>Mostra uma mensagem. Com <paramref name="ask"/> pergunta e devolve true so para Sim.</summary>
    public static bool Alert(string message, string? title = null, bool ask = false)
    {
        var buttons = ask ? System.Windows.Forms.MessageBoxButtons.YesNo : System.Windows.Forms.MessageBoxButtons.OK;
        var icon = ask ? System.Windows.Forms.MessageBoxIcon.Question : System.Windows.Forms.MessageBoxIcon.Information;
        var handle = RevitContext.MainWindowHandle;
        var result = handle == IntPtr.Zero
            ? System.Windows.Forms.MessageBox.Show(message, title ?? Title, buttons, icon)
            : System.Windows.Forms.MessageBox.Show(new Owner(handle), message, title ?? Title, buttons, icon);
        return !ask || result == System.Windows.Forms.DialogResult.Yes;
    }

    public static void Attach(Window window)
    {
        try
        {
            var handle = RevitContext.MainWindowHandle;
            if (handle != IntPtr.Zero) new WindowInteropHelper(window).Owner = handle;
        }
        catch (Exception)
        {
            // Sem dono a janela funciona do mesmo jeito; so pode abrir atras do Revit.
        }
    }

    public static string? PickFolder(string? start, string description)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        var handle = RevitContext.MainWindowHandle;
        var result = handle == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new Owner(handle));
        return result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public static string? PickFile(string filter, string title, string? startFolder = null)
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            CheckFileExists = true,
            Filter = filter,
            Title = title,
            InitialDirectory = Directory.Exists(startFolder) ? startFolder : "",
        };
        var handle = RevitContext.MainWindowHandle;
        var result = handle == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new Owner(handle));
        return result == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>Pede um texto curto (nome de colecao). Devolve null se cancelar.</summary>
    public static string? Prompt(string message, string title, string initial = "")
    {
        var box = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(4) };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 4, 0, 4) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", IsCancel = true, Width = 80, Padding = new Thickness(0, 4, 0, 4) };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = title, Content = panel, Width = 380, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
        };
        Attach(window);
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
