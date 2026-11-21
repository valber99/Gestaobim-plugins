using System.Windows;
using System.Windows.Interop;
using Gestaobim.DwgExport.Revit;

namespace Gestaobim.DwgExport.UI;

/// <summary>Message boxes and pickers, parented to Revit so they never hide behind it.</summary>
public static class Dialogs
{
    public const string Title = "GB DWG Export";

    private sealed class Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    /// <summary>Shows a message. With <paramref name="ask"/> it asks and returns true only for Yes.</summary>
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
            // Without an owner the window still works; it just may open behind Revit.
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

    public static string? PickFile(bool save, string filter, string title, string? fileName = null)
    {
        System.Windows.Forms.FileDialog dialog = save
            ? new System.Windows.Forms.SaveFileDialog { OverwritePrompt = true, FileName = fileName ?? "" }
            : new System.Windows.Forms.OpenFileDialog { CheckFileExists = true };
        using (dialog)
        {
            dialog.Filter = filter;
            dialog.Title = title;
            var handle = RevitContext.MainWindowHandle;
            var result = handle == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new Owner(handle));
            return result == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
        }
    }
}
