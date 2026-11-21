using System.Diagnostics;
using System.Net.Http;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Gestaobim.MaterialStudio.Core;

namespace Gestaobim.MaterialStudio.Addin;

/// <summary>
/// Asks GitHub (at most once a day, in the background) whether a newer release exists and, when it does,
/// tells the user once Revit is idle. Never blocks startup and stays silent if the network is unavailable.
/// </summary>
public static class UpdateNotifier
{
    private static ReleaseInfo? _pending;

    public static void Start(UIControlledApplication application)
    {
        if (Environment.GetEnvironmentVariable("GB_MATERIAL_STUDIO_NO_UPDATE_CHECK") == "1") return;
        var current = typeof(UpdateNotifier).Assembly.GetName().Version ?? new Version(0, 0, 0);
        application.Idling += OnIdling;
        _ = Task.Run(() => Check(current));
    }

    private static async Task Check(Version current)
    {
        try
        {
            var state = UpdateState.Load();
            if (!state.Due(DateTime.UtcNow)) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"GBMaterialStudio/{current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(UpdateRules.LatestReleaseApi).ConfigureAwait(false);

            state.LastCheckUtc = DateTime.UtcNow;
            state.Save();
            var release = UpdateRules.ParseNewer(json, current);
            if (release is not null && release.Tag != state.IgnoredTag) _pending = release;
        }
        catch (Exception)
        {
            // Offline, rate limited or GitHub down: try again on the next start.
        }
    }

    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        var release = Interlocked.Exchange(ref _pending, null);
        if (release is null) return;
        if (sender is UIApplication app) app.Idling -= OnIdling;
        Show(release);
    }

    private static void Show(ReleaseInfo release)
    {
        var dialog = new TaskDialog("Gestão BIM")
        {
            MainInstruction = $"Nova versão do GB Material Studio: {release.Version}",
            MainContent = "Há uma atualização no GitHub. Para instalar, baixe o .zip, feche o Revit e execute o Instalar.bat.",
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir página de download");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Lembrar depois", "Avisa de novo amanhã.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Ignorar esta versão", "Só avisa quando houver uma versão mais nova.");
        switch (dialog.Show())
        {
            case TaskDialogResult.CommandLink1:
                try
                {
                    Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
                }
                catch (Exception)
                {
                }
                break;
            case TaskDialogResult.CommandLink3:
                var state = UpdateState.Load();
                state.IgnoredTag = release.Tag;
                state.Save();
                break;
        }
    }
}
