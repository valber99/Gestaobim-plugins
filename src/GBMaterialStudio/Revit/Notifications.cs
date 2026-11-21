using System.Diagnostics;
using System.Security;
using System.Text;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>Windows toast notifications, shown through PowerShell so no extra package is needed.</summary>
public static class Notifications
{
    public const string AppName = "Gestão BIM";

    private static string IconFile =>
        Path.Combine(Path.GetDirectoryName(typeof(Notifications).Assembly.Location)!, "gestaobim.png");

    /// <summary>Fire and forget. A failure to show the toast never affects the operation that asked for it.</summary>
    public static void Toast(string message, string? title = null)
    {
        try
        {
            var xml = "<toast><visual><binding template=\"ToastGeneric\"><text>" + SecurityElement.Escape(title ?? AppName) +
                      "</text><text>" + SecurityElement.Escape(message) + "</text>" +
                      (File.Exists(IconFile) ? "<image placement=\"appLogoOverride\" src=\"" + SecurityElement.Escape(IconFile) + "\"/>" : "") +
                      "</binding></visual></toast>";
            var script = $@"$ErrorActionPreference='Stop'
$key = 'HKCU:\Software\Classes\AppUserModelId\GestaoBIM.Materials'
if ((Get-ItemProperty -Path $key -Name DisplayName -ErrorAction SilentlyContinue).DisplayName -ne '{AppName}') {{
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name DisplayName -Value '{AppName}' -PropertyType String -Force | Out-Null
}}
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] | Out-Null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml('{xml.Replace("'", "''")}')
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('GestaoBIM.Materials').Show($toast)
";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // Notifications are a convenience only.
        }
    }
}
