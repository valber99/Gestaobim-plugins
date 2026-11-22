using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace Gestaobim.Shared;

/// <summary>
/// O botao "Sobre" UNICO da aba Gestao BIM, compartilhado pelos tres plugins (Material Studio, DWG Export, Familias).
/// Cada plugin e uma DLL separada, entao este arquivo e compilado dentro de cada uma e as copias conversam por dados do
/// AppDomain (so tipos da biblioteca padrao, que tem a mesma identidade em todas as DLLs): o primeiro plugin a iniciar cria
/// o painel e o botao; os outros so se registram. Ao clicar, a janela mostra TODOS os plugins instalados, cada um com a
/// sua versao, o aviso de versao nova e o seu diagnostico. A bolinha vermelha acende se qualquer um tiver versao nova.
/// </summary>
public static class AboutHub
{
    public const string PanelName = "Geral";
    public const string ButtonName = "GBAboutAll";
    public const string Tooltip = "Mostra a versão instalada de cada plugin Gestão BIM e verifica se há versão nova no GitHub.";

    private const string ProvidersKey = "GestaoBIM.AboutHub.Providers";
    private const string UpdatesKey = "GestaoBIM.AboutHub.Updates";
    private const string ButtonKey = "GestaoBIM.AboutHub.Button";

    private static readonly object Gate = AppDomain.CurrentDomain; // mesmo objeto para todas as copias deste arquivo

    private static T Shared<T>(string key) where T : class, new()
    {
        lock (Gate)
        {
            if (AppDomain.CurrentDomain.GetData(key) is T existing) return existing;
            var created = new T();
            AppDomain.CurrentDomain.SetData(key, created);
            return created;
        }
    }

    private static ConcurrentDictionary<string, Func<(string Name, string Body, string? NewVersion, string? Url)>> Providers =>
        Shared<ConcurrentDictionary<string, Func<(string, string, string?, string?)>>>(ProvidersKey);

    private static ConcurrentDictionary<string, string> Updates => Shared<ConcurrentDictionary<string, string>>(UpdatesKey);

    private static PushButton? Button => AppDomain.CurrentDomain.GetData(ButtonKey) as PushButton;

    /// <summary>
    /// Registra o plugin e, se ainda nao existir, cria o painel "Geral" com o botao "Sobre". Chamar antes de criar o painel do
    /// proprio plugin (o painel "Geral" fica a esquerda) e depois de criar a aba. O botao executa o comando do primeiro
    /// plugin que iniciou; todos eles chamam <see cref="ShowAll"/>.
    /// </summary>
    public static void Register(UIControlledApplication app, string tabName, string pluginName,
        Func<(string Name, string Body, string? NewVersion, string? Url)> provider,
        Func<string, BitmapImage> icon, string assemblyPath, string aboutCommandClass)
    {
        Providers[pluginName] = provider;
        if (Button is not null) return;
        var panel = app.GetRibbonPanels(tabName).FirstOrDefault(p => p.Name == PanelName) ?? app.CreateRibbonPanel(tabName, PanelName);
        var data = new PushButtonData(ButtonName, "Sobre", assemblyPath, aboutCommandClass)
        {
            ToolTip = Tooltip,
            LargeImage = icon("sobre"),
            Image = icon("sobre"),
        };
        lock (Gate) AppDomain.CurrentDomain.SetData(ButtonKey, (PushButton)panel.AddItem(data));
    }

    /// <summary>Um plugin achou versao nova (consulta em segundo plano): acende a bolinha. Chamar na thread da interface.</summary>
    public static void MarkUpdate(string pluginName, string version, Func<string, BitmapImage> icon)
    {
        Updates[pluginName] = version;
        Apply(icon);
    }

    private static void Apply(Func<string, BitmapImage> icon)
    {
        var button = Button;
        if (button is null) return;
        var updates = Updates;
        var image = updates.IsEmpty ? "sobre" : "sobre-novo";
        button.LargeImage = icon(image);
        button.Image = icon(image);
        button.ToolTip = updates.IsEmpty
            ? Tooltip
            : "Há versão nova: " + string.Join("; ", updates.OrderBy(u => u.Key).Select(u => $"{u.Key} {u.Value}")) + ". Clique para ver.";
    }

    /// <summary>Clique no botao: consulta todos os plugins ao mesmo tempo e mostra uma janela so.</summary>
    public static void ShowAll(Func<string, BitmapImage> icon)
    {
        var providers = Providers.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        var tasks = providers.Select(p => Task.Run(() =>
        {
            try
            {
                return p.Value();
            }
            catch (Exception error)
            {
                return (p.Key, "Não foi possível obter as informações: " + error.Message, (string?)null, (string?)null);
            }
        })).ToArray();
        Task.WaitAll(tasks, TimeSpan.FromSeconds(30));
        var sections = tasks.Select((t, i) => t.IsCompletedSuccessfully
            ? t.Result
            : (providers[i].Key, "Sem resposta (a consulta demorou demais).", (string?)null, (string?)null)).ToList();

        foreach (var s in sections)
        {
            if (s.Item3 is { } version) Updates[s.Item1] = version;
            else Updates.TryRemove(s.Item1, out _);
        }
        Apply(icon);

        var dialog = new TaskDialog("Gestão BIM")
        {
            MainInstruction = "Gestão BIM",
            MainContent = string.Join("\n\n", sections.Select(s => $"{s.Item1}\n{s.Item2}")),
            FooterText = "Código-fonte e licença AGPL-3.0: github.com/valber99/Gestaobim-plugins",
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close,
        };
        var links = sections.Where(s => s.Item3 is not null && s.Item4 is not null).Take(4).ToList();
        TaskDialogCommandLinkId[] ids =
            [TaskDialogCommandLinkId.CommandLink1, TaskDialogCommandLinkId.CommandLink2, TaskDialogCommandLinkId.CommandLink3, TaskDialogCommandLinkId.CommandLink4];
        TaskDialogResult[] results =
            [TaskDialogResult.CommandLink1, TaskDialogResult.CommandLink2, TaskDialogResult.CommandLink3, TaskDialogResult.CommandLink4];
        for (var i = 0; i < links.Count; i++) dialog.AddCommandLink(ids[i], $"Baixar {links[i].Item1} {links[i].Item3}");

        var chosen = Array.IndexOf(results, dialog.Show());
        if (chosen < 0 || chosen >= links.Count) return;
        try
        {
            Process.Start(new ProcessStartInfo(links[chosen].Item4!) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }
}
