using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Gestaobim.DwgExport.UI;

/// <summary>
/// A small window that says what the export is doing. The Revit API only works on Revit's own thread, so the export
/// cannot run in the background; <see cref="Update"/> repaints the window between steps so it does not look frozen.
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly TextBlock _text = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(20, 18, 20, 6),
        Text = "Preparando a exportação...",
    };

    public ProgressWindow()
    {
        Title = "GB DWG Export";
        Width = 460;
        Height = 130;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;

        var panel = new StackPanel();
        panel.Children.Add(_text);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8, Margin = new Thickness(20, 8, 20, 0) });
        Content = panel;
        Dialogs.Attach(this);
    }

    public void Update(string message)
    {
        _text.Text = message;
        // Let WPF paint before the next blocking Revit call.
        Dispatcher.Invoke(DispatcherPriority.Render, () => { });
    }
}
