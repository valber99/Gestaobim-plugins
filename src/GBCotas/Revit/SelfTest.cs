using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.Ai.Revit;
using Gestaobim.Cotas.Core;

namespace Gestaobim.Cotas.Revit;

/// <summary>
/// Autoverificacao DENTRO de um Revit de verdade, sem interface e sem rede: so age quando GB_COTAS_SELFTEST aponta para a pasta do
/// relatorio e GB_COTAS_SELFTEST_MODELS lista projetos (separados por ';'). Cada projeto e aberto desanexado, nunca e salvo. Em ate
/// duas plantas com muitas paredes: extrai, planeja pelas regras (nao depende de IA), posiciona, cria as cotas e confere o criterio da
/// fase 4: 0 excecoes da API, 0 colisoes entre cadeias e 100% das cadeias criadas como elementos Dimension nativos. Grava o
/// autodim_audit.json de cada planta.
/// </summary>
internal static class SelfTest
{
    public const string EnvFolder = "GB_COTAS_SELFTEST";
    public const string EnvModels = "GB_COTAS_SELFTEST_MODELS";

    public static void Attach(UIControlledApplication ui)
    {
        if (Environment.GetEnvironmentVariable(EnvFolder) is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var log = new SelfTestReporter(Path.Combine(folder, "relatorio.txt"));
        SelfTestDialogs.AutoClose(ui, log);
        ui.ControlledApplication.ApplicationInitialized += (sender, _) =>
        {
            try
            {
                var models = (Environment.GetEnvironmentVariable(EnvModels) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                log.Check("projetos para testar", models.Length > 0, string.Join(", ", models));
                foreach (var model in models)
                {
                    try { RunModel((Application)sender!, model, folder, log); }
                    catch (Exception error) { log.Check($"{Path.GetFileNameWithoutExtension(model)}: projeto aberto e testado", false, error.GetType().Name + ": " + error.Message); }
                }
            }
            catch (Exception error)
            {
                log.Check("excecao nao tratada", false, error.ToString());
            }
            finally
            {
                log.Finish();
            }
        };
    }

    private static (int All, int Solid) CountWalls(Document doc, View view)
    {
        var walls = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>().ToList();
        return (walls.Count, walls.Count(w => w.WallType.Kind != WallKind.Curtain));
    }

    private static void RunModel(Application app, string model, string folder, SelfTestReporter log)
    {
        var name = Path.GetFileNameWithoutExtension(model);
        log.Info($"=== {name}");
        var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(model), new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets });
        try
        {
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan)
                .Select(v => (View: v, Walls: CountWalls(doc, v)))
                .Where(t => t.Walls.All >= 8)
                .OrderByDescending(t => t.Walls.Solid).ThenByDescending(t => t.Walls.All).Take(2).Select(t => ((View)t.View, t.Walls)).ToList();
            log.Check($"{name}: plantas com paredes", plans.Count > 0, string.Join(", ", plans.Select(p => $"{p.Item1.Name} ({p.Walls.All} paredes, {p.Walls.All - p.Walls.Solid} cortina)")));
            // Corte e elevacao ainda nao sao cotados: o certo e recusar antes de tocar no projeto, dizendo o motivo.
            if (new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType is ViewType.Section or ViewType.Elevation) is { } section)
            {
                try
                {
                    ViewExtractor.Extract(doc, section);
                    log.Check($"{name}/{section.Name}: corte recusado com o motivo", false, "a extracao aceitou o corte");
                }
                catch (InvalidOperationException error)
                {
                    log.Check($"{name}/{section.Name}: corte recusado com o motivo", error.Message.Contains("Cortes e elevações"), error.Message);
                }
            }
            var rules = new RuleProfile();
            var index = 0;
            foreach (var (view, walls) in plans)
            {
                index++;
                var label = $"{name}/{view.Name}";
                if (walls.Solid < 4)
                {
                    // Quase so parede-cortina: nao ha o que cotar pelas faces. O certo e recusar com uma mensagem que diga o motivo.
                    try
                    {
                        ViewExtractor.Extract(doc, view);
                        log.Check($"{label}: recusa planta so com parede-cortina", false, "a extracao nao recusou");
                    }
                    catch (InvalidOperationException error)
                    {
                        log.Check($"{label}: recusa planta so com parede-cortina, com o motivo", error.Message.Contains("cortina"), error.Message);
                    }
                    continue;
                }
                try
                {
                    var extraction = ViewExtractor.Extract(doc, view);
                    var summary = extraction.Summary;
                    log.Check($"{label}: referencias extraidas", summary.Refs.Count >= 4,
                        $"{summary.Refs.Count} (paredes {summary.Refs.Count(r => r.Kind is RefKind.ExteriorFace or RefKind.InteriorFace)}, aberturas {summary.Refs.Count(r => r.Kind == RefKind.Opening)}, eixos {summary.Refs.Count(r => r.Kind == RefKind.Axis)}); obstaculos {summary.Obstacles.Count}");
                    foreach (var warning in extraction.Warnings) log.Info(warning);

                    var chains = RulePlanner.Plan(summary, rules);
                    var solved = DimSolver.Solve(chains, summary, rules);
                    var audit = DimSolver.Audit(solved, summary, rules);
                    log.Check($"{label}: cadeias planejadas e posicionadas", solved.Placed.Count > 0, $"{chains.Count} planejadas, {solved.Placed.Count} posicionadas, {solved.Unplaced.Count} nao couberam");
                    log.Check($"{label}: 0 colisoes AABB", audit.ChainCollisions == 0 && audit.ObstacleCollisions == 0, $"entre cadeias {audit.ChainCollisions}, com obstaculos {audit.ObstacleCollisions}");

                    var before = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).GetElementCount();
                    var applied = DimensionWriter.Apply(doc, view, extraction, solved.Placed, rules);
                    var after = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).GetElementCount();
                    foreach (var created in applied.Created)
                    {
                        var chain = solved.Placed.First(p => p.Chain.Id == created.ChainId);
                        var element = doc.GetElement(new ElementId(created.DimensionId)) as Dimension;
                        log.Info($"  {created.ChainId}: existe={element is { IsValidObject: true }} segmentos={element?.Segments.Size} valor={(element?.Value is { } v ? (v * 304.8).ToString("0.#") : "nulo")} planejado={string.Join("+", chain.SegmentLengths.Select(l => l.ToString("0.#")))} desvio={created.MaxDeviationMm:0.#} refs={string.Join(",", chain.Refs.Select(r => r.Id + ":" + r.Kind + "@" + r.Pos.ToString("0.#")))}");
                    }
                    log.Check($"{label}: cotas criadas", applied.Created.Count == solved.Placed.Count && applied.Failed.Count == 0,
                        $"{applied.Created.Count} de {solved.Placed.Count}; recusadas: {string.Join("; ", applied.Failed.Select(f => f.ChainId + ": " + f.Reason))}");
                    log.Check($"{label}: 100% como elementos Dimension nativos", after - before == applied.Created.Count && applied.Created.All(c => doc.GetElement(new ElementId(c.DimensionId)) is Dimension),
                        $"antes {before}, depois {after}");
                    log.Check($"{label}: valores do Revit batem com o plano (<= 2 mm)", applied.Created.All(c => c.MaxDeviationMm <= 2.0),
                        string.Join("; ", applied.Created.Where(c => c.MaxDeviationMm > 2.0).Select(c => $"{c.ChainId}: {c.MaxDeviationMm:0.#} mm")));
                    var json = AuditWriter.Build(view.Name, summary.Scale, new PlanOutcome(chains, [], PlanSource.Rules, false, null), solved, audit, applied, extraction.Warnings);
                    AuditWriter.Save(json, Path.Combine(folder, name, $"vista-{index}", "autodim_audit.json"));
                }
                catch (Exception error)
                {
                    log.Check($"{label}: sem excecao da API", false, error.ToString());
                }
            }
        }
        finally
        {
            doc.Close(false);
        }
    }
}
