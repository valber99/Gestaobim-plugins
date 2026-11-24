using System.Diagnostics;
using System.Net.Http;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Gestaobim.Shared;
using Gestaobim.MaterialStudio.Core;

namespace Gestaobim.MaterialStudio.Addin;

/// <summary>
/// Looks for a newer GitHub release. At startup it asks in the background (at most once a day) and, if a
/// newer version exists, puts a red dot on the "Sobre" ribbon button. The button itself checks on demand.
/// Never blocks startup and stays silent if the network is unavailable.
/// </summary>
public static class UpdateNotifier
{
    private const string DisplayName = "GB Material Studio";

    private static ReleaseInfo? _pending;

    private static Version Current => typeof(UpdateNotifier).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static void Start(UIControlledApplication application)
    {
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
        AboutHub.MarkUpdate(DisplayName, release.Version.ToString(3), App.Icon);
    }

    /// <summary>Consulta o GitHub agora e devolve a secao deste plugin para a janela unica "Sobre" (ver AboutHub).</summary>
    public static (string Name, string Body, string? NewVersion, string? Url) BuildAboutSection()
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

        var lines = new List<string> { $"Versão instalada: {UpdateRules.Normalize(Current).ToString(3)}" };
        if (failure is not null) lines.Add(failure);
        lines.AddRange(Details());
        return (DisplayName, string.Join("\n", lines), release?.Version.ToString(3), release?.Url);
    }

    private static IEnumerable<string> Details()
    {
        yield break;
    }
}
