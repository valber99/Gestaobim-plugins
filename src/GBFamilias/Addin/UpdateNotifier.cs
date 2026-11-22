using System.Diagnostics;
using System.Net.Http;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Gestaobim.Shared;
using Gestaobim.Familias.Core.Server;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Revit;

namespace Gestaobim.Familias.Addin;

/// <summary>
/// Procura uma versao nova deste plugin no GitHub. Ao iniciar pergunta em segundo plano (no maximo uma vez por dia) e,
/// se houver versao mais nova, poe uma bolinha vermelha no botao "Sobre". O proprio botao consulta na hora.
/// Nunca trava a inicializacao e fica quieto se a rede nao estiver disponivel.
/// </summary>
public static class UpdateNotifier
{
    private const string DisplayName = "GB Famílias";

    private static ReleaseInfo? _pending;

    private static Version Current => typeof(UpdateNotifier).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static void Start(UIControlledApplication application)
    {
        if (Environment.GetEnvironmentVariable("GB_FAMILIAS_NO_UPDATE_CHECK") == "1") return;
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
            // Sem rede, limite de consultas ou GitHub fora do ar: tenta de novo no proximo inicio ou pelo botao.
        }
    }

    private static async Task<ReleaseInfo?> FetchNewer()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GBFamilias/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync(UpdateRules.ReleasesApi).ConfigureAwait(false);
        return UpdateRules.ParseNewer(json, Current);
    }

    /// <summary>A faixa so pode ser alterada na thread de interface do Revit, entao a bolinha e aplicada aqui.</summary>
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
        lines.Add(release is not null
            ? $"Nova versão disponível: {release.Version.ToString(3)}. Para atualizar, baixe o instalador, feche o Revit e execute-o."
            : failure ?? "Você está na versão mais recente.");
        lines.AddRange(Details());
        return (DisplayName, string.Join("\n", lines), release?.Version.ToString(3), release?.Url);
    }

    private static IEnumerable<string> Details()
    {
        var settings = FamiliasSettings.Load();
        if (settings.Mode == LibraryMode.Server)
        {
            var catalog = CatalogSync.LocalVersion(settings.EffectiveDb);
            yield return catalog > 0
                ? $"Catálogo: versão {catalog}" + (LibraryService.Current is { } open ? $", {open.Index.All.Count} famílias (biblioteca aberta). Busca por IA: {open.SemanticDetail}" : "")
                : "Catálogo NÃO instalado: rode o instalador de novo.";
            yield return string.IsNullOrWhiteSpace(settings.DownloadsFolder)
                ? "Pasta das famílias baixadas: ainda não escolhida."
                : $"Pasta das famílias baixadas: {settings.DownloadsFolder}";
            yield return LibraryService.GetSession().Auth.HasSession ? "Conta: conectada ao servidor." : "Conta: não conectada (o login é pedido ao baixar uma família).";
        }
        else
        {
            yield return LibraryService.Current is { } lib
                ? $"Biblioteca aberta: {lib.Index.All.Count} famílias. Busca por IA: {lib.SemanticDetail}"
                : settings.Problems().Count == 0
                    ? "Biblioteca encontrada (abra o painel Biblioteca para carregar)."
                    : "Biblioteca NÃO encontrada: " + settings.Problems()[0];
        }
        yield return settings.ResolveGbfam(Path.GetDirectoryName(typeof(UpdateNotifier).Assembly.Location)) is not null
            ? "(motor de busca por IA encontrado)"
            : "(motor de busca por IA NÃO encontrado: rode o instalador de novo)";
    }
}
