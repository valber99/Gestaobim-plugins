using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Core.Server;
using Gestaobim.Familias.Revit;

namespace Gestaobim.Familias.UI;

/// <summary>Os passos do modo servidor que precisam de uma janela: entrar, baixar o catalogo e atualizar.</summary>
public static class ServerFlows
{
    /// <summary>Janela modal simples com andamento e Cancelar. Devolve o erro (ou null se terminou bem).</summary>
    public static Exception? RunWithProgress(Window? owner, string title, string message, Func<CancellationToken, IProgress<string>, Task> work)
    {
        using var cts = new CancellationTokenSource();
        Exception? failure = null;

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var bar = new ProgressBar { IsIndeterminate = true, Height = 8, Margin = new Thickness(0, 0, 0, 14) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Width = 90, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(0, 5, 0, 5) };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(text);
        panel.Children.Add(bar);
        panel.Children.Add(cancel);

        var window = new Window
        {
            Title = title, Content = panel, Width = 460, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
        };
        if (owner is not null) window.Owner = owner; else Dialogs.Attach(window);

        cancel.Click += (_, _) => cts.Cancel();
        window.Closing += (_, _) => cts.Cancel();
        var progress = new Progress<string>(m => text.Text = m);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await work(cts.Token, progress);
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                if (window.IsVisible) window.Close();
            }
        };
        window.ShowDialog();
        return failure;
    }

    /// <summary>Abre o navegador para o login e espera. True se entrou.</summary>
    public static bool SignIn(Window? owner)
    {
        var session = LibraryService.GetSession();
        var error = RunWithProgress(owner, "GB Famílias — Entrar",
            "Conclua o login na janela do navegador que acabou de abrir.\n\nEsta janela fecha sozinha quando terminar.",
            (cancel, _) => session.LoginAsync(cancel));
        if (error is null) return true;
        if (error is not OperationCanceledException) Dialogs.Alert(ServerMessages.Describe(error), "GB Famílias — Entrar");
        return false;
    }

    /// <summary>Primeiro uso: entra (se preciso) e baixa o catalogo. True se o catalogo local ficou pronto.</summary>
    public static bool EnsureCatalog(Window? owner, FamiliasSettings settings)
    {
        var session = LibraryService.GetSession();
        if (!session.Auth.HasSession && !SignIn(owner)) return false;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            SyncResult? result = null;
            var error = RunWithProgress(owner, "GB Famílias — Baixando o catálogo",
                "Baixando o catálogo de famílias (previews e dados de busca). Só na primeira vez, e depois só o que mudou.",
                async (cancel, _) => result = await session.CheckForUpdatesAsync(settings.EffectiveDb, settings.SyncEveryHours, true, cancel));
            if (error is not null)
            {
                if (error is not OperationCanceledException) Dialogs.Alert(ServerMessages.Describe(error));
                return false;
            }
            if (result is { Outcome: SyncOutcome.NeedsLogin } && attempt == 0 && SignIn(owner)) continue;
            if (result is not { Outcome: SyncOutcome.Ready or SyncOutcome.UpToDate })
            {
                Dialogs.Alert(result?.Message ?? "Não foi possível baixar o catálogo.", "GB Famílias");
                return false;
            }
            break;
        }

        ServerSession.ApplyPending(settings.EffectiveDb);
        return File.Exists(settings.EffectiveDb);
    }
}
