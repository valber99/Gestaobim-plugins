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
/// Pedido do arquiteto, "Planejar" (IA ou regras), lista de cadeias com caixa de marcar e "Criar cotas". A lista e o preview:
/// cada linha diz de que lado a cadeia fica, o nivel, quantas referencias e o comprimento total; cadeias que nao couberam
/// aparecem com o motivo e nao podem ser marcadas.
/// </summary>
public static class CotasWindow
{
    public static CotasChoice? Show(Extraction extraction, RuleProfile rules, string viewName)
    {
        var summary = extraction.Summary;
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = $"Vista: {viewName} (1:{summary.Scale:0}). {summary.Refs.Count} referências encontradas: " +
                   $"{summary.Refs.Count(r => r.Kind is RefKind.ExteriorFace or RefKind.InteriorFace)} faces de parede, " +
                   $"{summary.Refs.Count(r => r.Kind == RefKind.Opening)} planos de portas e janelas, {summary.Refs.Count(r => r.Kind == RefKind.Axis)} eixos.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var warning in extraction.Warnings)
            root.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });

        root.Children.Add(new TextBlock { Text = "O que cotar", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
        var request = new TextBox
        {
            Text = "Cotar as fachadas em três linhas (vãos, eixos e total) no padrão do escritório.",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 54, Padding = new Thickness(4),
        };
        root.Children.Add(request);
        var useAi = new CheckBox { Content = "Usar IA para planejar as cadeias (sem marcar, valem só as regras do perfil)", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(useAi);
        root.Children.Add(new TextBlock
        {
            Text = "A IA só escolhe quais referências formam cada cadeia. Posições, afastamentos e a ausência de colisões são calculados pelo programa.",
            FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });

        var plan = new Button { Content = "Planejar", Padding = new Thickness(18, 5, 18, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var listScroll = new ScrollViewer { Content = list, Height = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Background = Brushes.White };
        root.Children.Add(plan);
        root.Children.Add(status);
        root.Children.Add(listScroll);

        var create = new Button { Content = "Criar cotas", IsDefault = true, IsEnabled = false, Width = 110, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 5, 0, 5) };
        var rulesButton = new Button { Content = "Regras…", Width = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 5, 0, 5), ToolTip = "Abre o perfil de regras (afastamentos, lados e padrão do escritório)." };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Width = 90, Padding = new Thickness(0, 5, 0, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(rulesButton);
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        var window = new Window
        {
            Title = "GB Cotas — Cotar vista", Content = root, Width = 680, SizeToContent = SizeToContent.Height, MaxHeight = 820,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFA)),
        };
        Dialogs.Attach(window);

        PlanOutcome? outcome = null;
        SolverResult? solved = null;
        var boxes = new List<(CheckBox Box, PlacedChain Chain)>();
        CotasChoice? result = null;

        rulesButton.Click += (_, _) =>
        {
            try { CotasFlow.OpenProfile(); }
            catch (Exception error) { Dialogs.Alert(error.Message, CotasFlow.Title); }
        };

        plan.Click += async (_, _) =>
        {
            plan.IsEnabled = false;
            create.IsEnabled = false;
            list.Children.Clear();
            boxes.Clear();
            status.Foreground = Brushes.DarkSlateGray;
            status.Text = useAi.IsChecked == true ? "Conectando à IA e planejando…" : "Planejando pelas regras…";
            try
            {
                AiConnection? connection = null;
                string? connectionNote = null;
                if (useAi.IsChecked == true)
                {
                    try { connection = await AiService.ConnectAsync(); }
                    catch (Exception error) when (error is GatewayException or AiException)
                    {
                        connectionNote = "Sem IA (" + error.Message + "). Usei as regras do escritório.";
                    }
                }
                var model = connection?.PlanModel ?? "";
                if (connection is not null && model.Length == 0) connectionNote = AiService.NoModelsMessage + " Usei as regras do escritório.";
                try { outcome = await AiPlanner.PlanAsync(connection?.Client, model, summary, rules, request.Text); }
                finally { connection?.Dispose(); }
                if (connectionNote is not null) outcome = outcome with { Note = connectionNote };

                solved = DimSolver.Solve(outcome.Chains, summary, rules);
                foreach (var placed in solved.Placed.OrderBy(p => p.Chain.Side).ThenBy(p => p.Chain.Level))
                {
                    var length = placed.Refs[^1].Pos - placed.Refs[0].Pos;
                    var box = new CheckBox
                    {
                        IsChecked = true, Margin = new Thickness(6, 3, 6, 0),
                        Content = $"{SideName(placed.Chain.Side)} · {LevelName(placed.Chain.Level)} · {placed.Refs.Count} referências · {placed.SegmentLengths.Count} vão(s) · total {length:#,0} mm"
                                  + (placed.ShiftedSegments.Count > 0 ? $" · {placed.ShiftedSegments.Count} texto(s) deslocado(s)" : "")
                                  + (placed.Attempts > 1 ? " · afastada para evitar colisão" : ""),
                    };
                    list.Children.Add(box);
                    boxes.Add((box, placed));
                }
                foreach (var un in solved.Unplaced)
                    list.Children.Add(new TextBlock { Text = $"✗ {SideName(un.Chain.Side)} · {LevelName(un.Chain.Level)}: {un.Reason}", Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 3, 6, 0) });
                foreach (var rej in outcome.Rejected)
                    list.Children.Add(new TextBlock { Text = $"✗ Plano da IA, cadeia \"{rej.ChainId}\": {rej.Message}", Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 3, 6, 0) });

                var source = outcome.Source switch
                {
                    PlanSource.Ai => outcome.Retried ? "plano da IA (corrigido numa segunda tentativa)" : "plano da IA",
                    PlanSource.AiThenRules => "regras do escritório (a IA não deu um plano válido)",
                    _ => "regras do escritório",
                };
                status.Foreground = solved.Placed.Count > 0 ? Brushes.DarkGreen : Brushes.Firebrick;
                status.Text = $"{solved.Placed.Count} cadeia(s) posicionada(s) sem colisão com {source}." + (outcome.Note is null ? "" : " " + outcome.Note);
                create.IsEnabled = solved.Placed.Count > 0;
            }
            catch (Exception error)
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = error.Message;
            }
            finally
            {
                plan.IsEnabled = true;
            }
        };

        create.Click += (_, _) =>
        {
            var selected = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Chain).ToList();
            if (selected.Count == 0 || outcome is null || solved is null)
            {
                status.Foreground = Brushes.Firebrick;
                status.Text = "Marque pelo menos uma cadeia.";
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
