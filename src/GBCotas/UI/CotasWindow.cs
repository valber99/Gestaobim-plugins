using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gestaobim.Ai;
using Gestaobim.Ai.Revit;
using Gestaobim.Cotas.Core;
using Gestaobim.Cotas.Revit;

namespace Gestaobim.Cotas.UI;

/// <summary>O que o usuario aprovou: o plano, o resultado do solver e as cadeias marcadas.</summary>
public sealed class CotasChoice
{
    public required PlanOutcome Plan { get; init; }
    public required SolverResult Result { get; init; }
    public required IReadOnlyList<PlacedChain> Selected { get; init; }
}

/// <summary>
/// Janela do GB Cotas: visual moderno em cards, cancelamento seguro ao fechar, selecao rapida de cadeias
/// e preview antes de gravar cotas nativas no Revit.
/// </summary>
public static class CotasWindow
{
    public static CotasChoice? Show(Extraction extraction, RuleProfile rules, string viewName)
    {
        var summary = extraction.Summary;
        using var windowCts = new CancellationTokenSource();
        var closed = false;

        var content = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        foreach (var warning in extraction.Warnings)
        {
            content.Children.Add(new Border
            {
                Background = AiUi.WarnBg,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock { Text = warning, FontSize = 11.5, Foreground = AiUi.WarnFg, TextWrapping = TextWrapping.Wrap },
            });
        }

        // Card 1: Pedido e planejamento
        var reqPanel = new StackPanel();
        var request = new TextBox
        {
            Text = "Cotar as fachadas em três linhas (vãos, eixos e total) no padrão do escritório.",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 56,
            Padding = new Thickness(6),
            BorderBrush = AiUi.SecondaryBorder,
        };
        reqPanel.Children.Add(request);

        var useAi = new CheckBox
        {
            Content = "Usar IA para planejar as cadeias (desmarcado = usa apenas as regras do escritório)",
            IsChecked = true,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = AiUi.TitleFg,
        };
        reqPanel.Children.Add(useAi);
        reqPanel.Children.Add(AiUi.Note("A IA só escolhe quais referências formam cada cadeia. Posições, afastamentos e ausência de colisões são calculados pelo programa."));

        var plan = AiUi.PrimaryButton("Planejar cotas");
        plan.Margin = new Thickness(0, 0, 8, 0);
        var configBtn = AiUi.SecondaryButton("Configurar IA…");
        var planRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        planRow.Children.Add(plan);
        planRow.Children.Add(configBtn);
        reqPanel.Children.Add(planRow);

        var (statusBox, statusText, statusBar) = AiUi.StatusBanner();
        reqPanel.Children.Add(statusBox);
        content.Children.Add(AiUi.Card("O que cotar", reqPanel));

        // Card 2: Cadeias planejadas
        var listPanel = new StackPanel();
        var selectAll = AiUi.SecondaryButton("Marcar todas");
        selectAll.Padding = new Thickness(8, 3, 8, 3);
        selectAll.FontSize = 11.5;
        selectAll.Margin = new Thickness(0, 0, 6, 0);
        var selectNone = AiUi.SecondaryButton("Desmarcar todas");
        selectNone.Padding = new Thickness(8, 3, 8, 3);
        selectNone.FontSize = 11.5;
        var selRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
        selRow.Children.Add(selectAll);
        selRow.Children.Add(selectNone);
        listPanel.Children.Add(selRow);

        var list = new StackPanel { Margin = new Thickness(4) };
        var listScroll = new ScrollViewer
        {
            Content = list,
            Height = 250,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderBrush = AiUi.CardBorder,
            BorderThickness = new Thickness(1),
            Background = AiUi.WindowBg,
        };
        listPanel.Children.Add(listScroll);
        content.Children.Add(AiUi.Card("Cadeias planejadas (prévia)", listPanel, new Thickness(0)));

        // Rodape
        var rulesButton = AiUi.SecondaryButton("Abrir regras…");
        rulesButton.ToolTip = "Abre o perfil de regras (afastamentos, lados e padrão do escritório).";
        var create = AiUi.PrimaryButton("Criar cotas", isDefault: true);
        create.IsEnabled = false;
        create.Width = 116;
        create.Margin = new Thickness(0, 0, 8, 0);
        var cancel = AiUi.SecondaryButton("Cancelar", isCancel: true);
        cancel.Width = 92;

        var footerDock = new DockPanel { Margin = new Thickness(16, 10, 16, 10) };
        DockPanel.SetDock(rulesButton, Dock.Left);
        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightBtns.Children.Add(create);
        rightBtns.Children.Add(cancel);
        DockPanel.SetDock(rightBtns, Dock.Right);
        footerDock.Children.Add(rulesButton);
        footerDock.Children.Add(rightBtns);

        var footer = new Border
        {
            Background = Brushes.White,
            BorderBrush = AiUi.CardBorder,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerDock,
        };

        var subtitle = $"Vista: {viewName} (1:{summary.Scale:0})  ·  {summary.Refs.Count} referências (" +
                       $"{summary.Refs.Count(r => r.Kind is RefKind.ExteriorFace or RefKind.InteriorFace)} faces, " +
                       $"{summary.Refs.Count(r => r.Kind == RefKind.Opening)} aberturas, {summary.Refs.Count(r => r.Kind == RefKind.Axis)} eixos)";
        var header = AiUi.Header("GB Cotas — Cotar vista", subtitle);

        var mainDock = new DockPanel { Background = AiUi.WindowBg };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        mainDock.Children.Add(header);
        mainDock.Children.Add(footer);
        mainDock.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var window = new Window
        {
            Title = "GB Cotas — Cotar vista",
            Content = mainDock,
            Width = 720,
            Height = 720,
            MinWidth = 600,
            MinHeight = 560,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Background = AiUi.WindowBg,
        };
        Dialogs.Attach(window);

        window.Closing += (_, _) =>
        {
            closed = true;
            try { windowCts.Cancel(); } catch { }
        };

        PlanOutcome? outcome = null;
        SolverResult? solved = null;
        var boxes = new List<(CheckBox Box, PlacedChain Chain)>();
        CotasChoice? result = null;

        selectAll.Click += (_, _) => { foreach (var b in boxes) b.Box.IsChecked = true; };
        selectNone.Click += (_, _) => { foreach (var b in boxes) b.Box.IsChecked = false; };
        configBtn.Click += (_, _) => AiSettingsWindow.ShowDialog();

        rulesButton.Click += (_, _) =>
        {
            try { CotasFlow.OpenProfile(); }
            catch (Exception error) { MessageBox.Show(window, error.Message, CotasFlow.Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        };

        plan.Click += async (_, _) =>
        {
            if (closed) return;
            plan.IsEnabled = false;
            configBtn.IsEnabled = false;
            create.IsEnabled = false;
            selRow.Visibility = Visibility.Collapsed;
            list.Children.Clear();
            boxes.Clear();
            AiUi.SetStatus(statusBox, statusText, statusBar, useAi.IsChecked == true ? "Conectando à IA e planejando cadeias…" : "Planejando pelas regras do escritório…", StatusKind.Info, busy: true);
            try
            {
                AiConnection? connection = null;
                string? connectionNote = null;
                if (useAi.IsChecked == true)
                {
                    try { connection = await AiService.ConnectAsync(windowCts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception error) when (error is GatewayException or AiException)
                    {
                        connectionNote = "Sem IA (" + error.Message + "). Usei as regras do escritório.";
                    }
                }
                if (closed) { connection?.Dispose(); return; }
                var model = connection?.PlanModel ?? "";
                if (connection is not null && model.Length == 0) connectionNote = AiService.NoModelsMessage + " Usei as regras do escritório.";
                try { outcome = await AiPlanner.PlanAsync(connection?.Client, model, summary, rules, request.Text, windowCts.Token); }
                finally { connection?.Dispose(); }
                if (closed) return;
                if (connectionNote is not null) outcome = outcome with { Note = connectionNote };

                solved = DimSolver.Solve(outcome.Chains, summary, rules);
                foreach (var placed in solved.Placed.OrderBy(p => p.Chain.Side).ThenBy(p => p.Chain.Level))
                {
                    var length = placed.Refs[^1].Pos - placed.Refs[0].Pos;
                    var box = new CheckBox
                    {
                        IsChecked = true,
                        Margin = new Thickness(6, 4, 6, 2),
                        Foreground = AiUi.TitleFg,
                        Content = $"{SideName(placed.Chain.Side)} · {LevelName(placed.Chain.Level)} · {placed.Refs.Count} refs · {placed.SegmentLengths.Count} vão(s) · {length:#,0} mm"
                                  + (placed.ShiftedSegments.Count > 0 ? $" · {placed.ShiftedSegments.Count} texto(s) deslocado(s)" : "")
                                  + (placed.Attempts > 1 ? " · afastada p/ evitar colisão" : ""),
                    };
                    list.Children.Add(box);
                    boxes.Add((box, placed));
                }
                foreach (var un in solved.Unplaced)
                    list.Children.Add(new TextBlock { Text = $"✗ {SideName(un.Chain.Side)} · {LevelName(un.Chain.Level)}: {un.Reason}", Foreground = AiUi.ErrFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 2) });
                foreach (var rej in outcome.Rejected)
                    list.Children.Add(new TextBlock { Text = $"✗ Plano da IA, cadeia \"{rej.ChainId}\": {rej.Message}", Foreground = AiUi.WarnFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 2) });

                var source = outcome.Source switch
                {
                    PlanSource.Ai => outcome.Retried ? "plano da IA (corrigido numa 2ª tentativa)" : "plano da IA",
                    PlanSource.AiThenRules => "regras do escritório (a IA não deu um plano válido)",
                    _ => "regras do escritório",
                };
                var kind = solved.Placed.Count > 0 ? StatusKind.Ok : StatusKind.Error;
                AiUi.SetStatus(statusBox, statusText, statusBar,
                    $"{solved.Placed.Count} cadeia(s) posicionada(s) sem colisão com {source}." + (outcome.Note is null ? "" : " " + outcome.Note),
                    kind);
                create.IsEnabled = solved.Placed.Count > 0;
                selRow.Visibility = boxes.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!closed) AiUi.SetStatus(statusBox, statusText, statusBar, error.Message, StatusKind.Error);
            }
            finally
            {
                if (!closed) { plan.IsEnabled = true; configBtn.IsEnabled = true; }
            }
        };

        create.Click += (_, _) =>
        {
            var selected = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Chain).ToList();
            if (selected.Count == 0 || outcome is null || solved is null)
            {
                AiUi.SetStatus(statusBox, statusText, statusBar, "Marque pelo menos uma cadeia para criar.", StatusKind.Warn);
                return;
            }
            result = new CotasChoice { Plan = outcome, Result = solved, Selected = selected };
            window.DialogResult = true;
        };

        window.ShowDialog();
        return result;
    }

    private static string SideName(Side side) => side switch { Side.Top => "Em cima", Side.Bottom => "Embaixo", Side.Left => "À esquerda", _ => "À direita" };

    private static string LevelName(Level level) => level switch { Level.Internal => "interna (vãos)", Level.Intermediate => "intermediária (eixos e paredes)", _ => "externa (total)" };
}
