using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Gestaobim.MaterialStudio.Revit;
using FormsDialogResult = System.Windows.Forms.DialogResult;

namespace Gestaobim.MaterialStudio.UI;

/// <summary>Message boxes and pickers, parented to Revit so they never hide behind it.</summary>
public static class Dialogs
{
    public const string Title = "Gestão BIM";

    private sealed class Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    private static IntPtr RevitHandle()
    {
        try { return RevitContext.MainWindowHandle; }
        catch (InvalidOperationException) { return IntPtr.Zero; }
    }

    /// <summary>Shows a message. With <paramref name="ask"/> it asks and returns true only for Yes.</summary>
    public static bool Alert(string message, string? title = null, bool ask = false)
    {
        var buttons = ask ? System.Windows.Forms.MessageBoxButtons.YesNo : System.Windows.Forms.MessageBoxButtons.OK;
        var icon = ask ? System.Windows.Forms.MessageBoxIcon.Question : System.Windows.Forms.MessageBoxIcon.Information;
        var handle = RevitHandle();
        var result = handle == IntPtr.Zero
            ? System.Windows.Forms.MessageBox.Show(message, title ?? Title, buttons, icon)
            : System.Windows.Forms.MessageBox.Show(new Owner(handle), message, title ?? Title, buttons, icon);
        return !ask || result == FormsDialogResult.Yes;
    }

    public static void Attach(Window window)
    {
        try
        {
            var handle = RevitHandle();
            if (handle != IntPtr.Zero)
                new WindowInteropHelper(window).Owner = handle;
        }
        catch (Exception)
        {
            // Without an owner the window still works; it just may open behind Revit.
        }
    }

    private static Window Build(string title, string prompt, UIElement body, string okText, out Button ok)
    {
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 420,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Background = Visuals.ToBrush("#F4F6F7"),
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        if (prompt.Length > 0)
            panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(body);
        ok = new Button { Content = okText, IsDefault = true, MinWidth = 96, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 96, Padding = new Thickness(12, 7, 12, 7) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        Attach(window);
        return window;
    }

    /// <summary>Asks for a line of text. Returns null when cancelled.</summary>
    public static string? AskString(string prompt, string defaultText = "", string? title = null)
    {
        var input = new TextBox { Text = defaultText, MinHeight = 32, Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center };
        var window = Build(title ?? Title, prompt, input, "OK", out var ok);
        ok.Click += (_, _) => window.DialogResult = true;
        input.SelectAll();
        input.Loaded += (_, _) => input.Focus();
        return window.ShowDialog() == true ? input.Text : null;
    }

    /// <summary>Asks for a colour. Returns "#RRGGBB" or null when cancelled.</summary>
    public static string? AskColor()
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
        var handle = RevitHandle();
        var result = handle == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new Owner(handle));
        return result == FormsDialogResult.OK ? Visuals.Hex(dialog.Color.R, dialog.Color.G, dialog.Color.B) : null;
    }

    /// <summary>Picks a file. <paramref name="extensions"/> is a ';'-separated list such as "jpg;png".</summary>
    public static string? PickFile(string extensions, string title = "Selecionar arquivo")
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title };
        var list = extensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('.')).ToList();
        if (list.Count > 0)
        {
            var pattern = string.Join(';', list.Select(e => "*." + e));
            dialog.Filter = $"Arquivos ({pattern})|{pattern}|Todos os arquivos|*.*";
        }
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public static string? SaveFile(string title, string fileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = fileName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Picks from a list of strings with a filter box. Items hidden by the filter stay selected.
    /// Returns the chosen items in list order, or an empty list when cancelled.
    /// </summary>
    public static IReadOnlyList<string> SelectFromList(IReadOnlyList<string> items, string title, bool multiple, string buttonName)
    {
        var filter = new TextBox { MinHeight = 32, Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8), ToolTip = "Digite para filtrar a lista" };
        var list = new ListBox { Height = 360, MinWidth = 620, SelectionMode = multiple ? SelectionMode.Extended : SelectionMode.Single };
        var body = new StackPanel();
        body.Children.Add(filter);
        body.Children.Add(list);
        var window = Build(title, "", body, buttonName, out var ok);

        var chosen = new HashSet<string>();
        var refreshing = false;

        void Show(IEnumerable<string> visible)
        {
            refreshing = true;
            try
            {
                var shown = visible.ToList();
                list.ItemsSource = shown;
                foreach (var item in shown.Where(chosen.Contains))
                    list.SelectedItems.Add(item);
            }
            finally
            {
                refreshing = false;
            }
        }

        filter.TextChanged += (_, _) =>
        {
            var text = filter.Text.Trim();
            Show(items.Where(i => i.Contains(text, StringComparison.OrdinalIgnoreCase)));
        };
        list.SelectionChanged += (_, args) =>
        {
            if (refreshing) return;
            foreach (string item in args.RemovedItems) chosen.Remove(item);
            foreach (string item in args.AddedItems) chosen.Add(item);
        };
        list.MouseDoubleClick += (_, _) =>
        {
            if (!multiple && chosen.Count > 0) window.DialogResult = true;
        };
        ok.Click += (_, _) => window.DialogResult = true;
        Show(items);
        filter.Loaded += (_, _) => filter.Focus();
        return window.ShowDialog() == true ? items.Where(chosen.Contains).ToList() : [];
    }
}
