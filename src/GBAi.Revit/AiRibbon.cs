using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Gestaobim.Shared;

namespace Gestaobim.Ai.Revit;

/// <summary>A aba "Gestão BIM" e o painel "IA" com o botao unico "Configurar IA" (o primeiro plugin de IA a iniciar o cria).</summary>
public static class AiRibbon
{
    public const string TabName = "Gestão BIM";
    public const string ConfigPanelName = "IA";
    public const string ConfigButtonName = "GBAiConfig";

    public static void EnsureTab(UIControlledApplication app)
    {
        try
        {
            app.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // A aba ja existe (outro plugin da Gestao BIM a criou): o painel vai para la.
        }
    }

    public static RibbonPanel Panel(UIControlledApplication app, string name) =>
        app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == name) ?? app.CreateRibbonPanel(TabName, name);

    public static void EnsureConfigButton(UIControlledApplication app, string assemblyPath, Func<string, BitmapImage> icon)
    {
        var panel = Panel(app, ConfigPanelName);
        if (panel.GetItems().Any(i => i.Name == ConfigButtonName)) return;
        panel.AddItem(new PushButtonData(ConfigButtonName, "Configurar\nIA", assemblyPath, typeof(ConfigureAiCommand).FullName!)
        {
            ToolTip = "Conecta as contas de IA (assinatura ou chave de API), escolhe o modelo de cada tarefa e testa o gateway local. Usado pelas Cotas e pelo Render.",
            LargeImage = icon("config-ia"),
            Image = icon("config-ia"),
        });
    }
}

/// <summary>Procura release nova no GitHub (uma vez por dia, em segundo plano) e alimenta o botao "Sobre" unico.</summary>
public sealed class AiUpdateNotifier(string displayName, Version current, Func<string, BitmapImage> icon, Func<string[]> details)
{
    private ReleaseInfo? _pending;

    public void Start(UIControlledApplication application)
    {
        if (Environment.GetEnvironmentVariable("GB_AI_NO_UPDATE_CHECK") == "1") return;
        application.Idling += OnIdling;
        _ = Task.Run(CheckInBackground);
    }

    private async Task CheckInBackground()
    {
        try
        {
            var state = AiUpdateState.Load();
            if (!state.Due(DateTime.UtcNow)) return;
            var release = await FetchNewer().ConfigureAwait(false);
            state.LastCheckUtc = DateTime.UtcNow;
            state.Save();
            if (release is not null) _pending = release;
        }
        catch (Exception)
        {
            // Sem rede, limite de consultas ou GitHub fora: tenta de novo no proximo inicio ou pelo botao.
        }
    }

    private async Task<ReleaseInfo?> FetchNewer()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GestaoBIM-{displayName.Replace(' ', '-')}/{current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync(AiUpdateRules.ReleasesApi).ConfigureAwait(false);
        return AiUpdateRules.ParseNewer(json, current);
    }

    private void OnIdling(object? sender, IdlingEventArgs e)
    {
        var release = Interlocked.Exchange(ref _pending, null);
        if (release is null) return;
        if (sender is UIApplication app) app.Idling -= OnIdling;
        AboutHub.MarkUpdate(displayName, release.Version.ToString(3), icon);
    }

    public (string Name, string Body, string? NewVersion, string? Url) BuildAboutSection()
    {
        ReleaseInfo? release = null;
        string? failure = null;
        try
        {
            release = Task.Run(FetchNewer).GetAwaiter().GetResult();
            var state = AiUpdateState.Load();
            state.LastCheckUtc = DateTime.UtcNow;
            state.Save();
        }
        catch (Exception)
        {
            failure = "Não foi possível consultar o GitHub. Verifique a conexão com a internet.";
        }
        var lines = new System.Collections.Generic.List<string> { $"Versão instalada: {AiUpdateRules.Normalize(current).ToString(3)}" };
        lines.Add(release is not null
            ? $"Nova versão disponível: {release.Version.ToString(3)}. Para atualizar, baixe o instalador, feche o Revit e execute-o."
            : failure ?? "Você está na versão mais recente.");
        lines.AddRange(details());
        return (displayName, string.Join("\n", lines), release?.Version.ToString(3), release?.Url);
    }
}
