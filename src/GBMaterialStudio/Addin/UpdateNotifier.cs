using System.Diagnostics;
using System.Net.Http;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Gestaobim.MaterialStudio.Core;

namespace Gestaobim.MaterialStudio.Addin;

/// <summary>
/// Looks for a newer GitHub release. At startup it asks in the background (at most once a day) and, if a
/// newer version exists, puts a red dot on the "Sobre" ribbon button. The button itself checks on demand.
/// Never blocks startup and stays silent if the network is unavailable.
/// </summary>
public static class UpdateNotifier
{
    public const string ButtonName = "GBAbout";

    private static ReleaseInfo? _pending;
    private static PushButton? _button;

    private static Version Current => typeof(UpdateNotifier).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static void Start(UIControlledApplication application, PushButton button)
    {
        _button = button;
        if (Environment.GetEnvironmentVariable("GB_MATERIAL_STUDIO_NO_UPDATE_CHECK") == "1") return;
        application.Idling += OnIdling;
        _ = Task.Run(CheckInBackground);
    }

    private static async Task CheckInBackground()
    {
        try
        {
            var state = UpdateState.Load();
            if (!state.Due(DateTime.UtcNow)) return;
            var release = await FetchNewer().ConfigureAwait(false);
            state.LastCheckUtc = DateTime.UtcNow;
            state.Save();
            if (release is not null) _pending = release;
        }
        catch (Exception)
        {
            // Offline, rate limited or GitHub down: try again on the next start or from the button.
        }
    }

    private static async Task<ReleaseInfo?> FetchNewer()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GBMaterialStudio/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync(UpdateRules.LatestReleaseApi).ConfigureAwait(false);
        return UpdateRules.ParseNewer(json, Current);
    }

    /// <summary>The ribbon can only be changed on Revit's UI thread, so the dot is applied here.</summary>
    private static void OnIdling(object? sender, IdlingEventArgs e)
    {
        var release = Interlocked.Exchange(ref _pending, null);
        if (release is null) return;
        if (sender is UIApplication app) app.Idling -= OnIdling;
        MarkAvailable(release);
    }

    private static void MarkAvailable(ReleaseInfo release)
    {
        if (_button is null) return;
        _button.LargeImage = App.Icon("sobre-novo");
        _button.Image = App.Icon("sobre-novo");
        _button.ToolTip = $"Nova versão disponível: {release.Version}. Clique para ver.";
    }

    private static void MarkUpToDate()
    {
        if (_button is null) return;
        _button.LargeImage = App.Icon("sobre");
        _button.Image = App.Icon("sobre");
        _button.ToolTip = App.AboutTooltip;
    }

    /// <summary>Button click: check now, then show the result.</summary>
    public static void ShowAbout()
    {
        ReleaseInfo? release = null;
        string? failure = null;
        try
        {
            release = Task.Run(FetchNewer).GetAwaiter().GetResult();
            var state = UpdateState.Load();
            state.LastCheckUtc = DateTime.UtcNow;
            state.Save();
        }
        catch (Exception)
        {
            failure = "Não foi possível consultar o GitHub. Verifique a conexão com a internet.";
        }

        var installed = UpdateRules.Normalize(Current);
        var dialog = new TaskDialog("Gestão BIM")
        {
            MainInstruction = "GB Material Studio",
            FooterText = "Código-fonte e licença AGPL-3.0: github.com/valber99/Gestaobim-plugins",
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close,
        };

        if (release is not null)
        {
            MarkAvailable(release);
            dialog.MainContent = $"Versão instalada: {installed}\nNova versão disponível: {release.Version}\n\n" +
                                 "Para atualizar, baixe o .zip, feche o Revit e execute o Instalar.bat.";
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Baixar a nova versão");
        }
        else
        {
            if (failure is null) MarkUpToDate();
            dialog.MainContent = $"Versão instalada: {installed}\n\n" + (failure ?? "Você está na versão mais recente.");
        }

        if (dialog.Show() == TaskDialogResult.CommandLink1 && release is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true });
            }
            catch (Exception)
            {
            }
        }
    }
}
