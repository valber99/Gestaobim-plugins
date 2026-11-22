using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.DwgExport.Bridge;
using Gestaobim.DwgExport.Core;

namespace Gestaobim.DwgExport.Revit;

/// <summary>
/// Autoverificacao da exportacao DENTRO de um Revit de verdade, sem interface. So age quando a variavel
/// GB_DWG_EXPORT_SELFTEST aponta para a pasta do relatorio, e GB_DWG_EXPORT_SELFTEST_MODELS lista projetos (separados
/// por ';'). Cada projeto e aberto desanexado, nunca e salvo, e e fechado no fim. Para cada um: folhas em cada modo de
/// mesclagem (inclusive com a configuracao de exportacao do proprio projeto) e vistas no model space. Confere os
/// layouts, as viewports e o carimbo copiados, e grava um PNG de cada layout para conferir a olho.
/// </summary>
internal static class SelfTest
{
    public const string EnvFolder = "GB_DWG_EXPORT_SELFTEST";
    public const string EnvModels = "GB_DWG_EXPORT_SELFTEST_MODELS";

    public static void Attach(UIControlledApplication ui)
    {
        if (Environment.GetEnvironmentVariable(EnvFolder) is not { Length: > 0 } folder) return;
        ui.ControlledApplication.ApplicationInitialized += (sender, _) =>
        {
            Directory.CreateDirectory(folder);
            var log = new Reporter(Path.Combine(folder, "relatorio.txt"));
            try
            {
                var models = (Environment.GetEnvironmentVariable(EnvModels) ?? "")
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                log.Check("projetos para testar", models.Length > 0, string.Join(", ", models));
                foreach (var model in models) RunModel((Application)sender!, model, folder, log);
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

    private static void RunModel(Application app, string model, string folder, Reporter log)
    {
        var name = Path.GetFileNameWithoutExtension(model);
        log.Info($"=== {name}");
        var options = new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets };
        var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(model), options);
        try
        {
            var sheets = SheetSource.Sheets(doc);
            // The two sheets with the most views, so a merge really has something to move around; kept in sheet order.
            var picked = sheets
                .Select((item, index) => (Item: item, Index: index, Views: doc.GetElement(item.Id) is ViewSheet sheet ? Placed(doc, sheet).Count : 0))
                .Where(entry => entry.Views > 0)
                .OrderByDescending(entry => entry.Views).ThenBy(entry => entry.Index).Take(2)
                .OrderBy(entry => entry.Index).Select(entry => entry.Item).ToList();
            log.Check($"{name}: folhas com vistas", picked.Count > 0, string.Join(", ", picked.Select(item => $"{item.Number} ({item.PaperWidthMm}x{item.PaperHeightMm} mm, canto {item.OriginXMm};{item.OriginYMm})")));
            if (picked.Count == 0) return;

            var setups = SheetSource.RevitSetups(doc);
            var runs = new List<(string Label, OutputMode Mode, string Setup)>
            {
                ("layouts", OutputMode.MultipleLayouts, ""),
                ("layout-unico", OutputMode.SingleLayout, ""),
                ("model", OutputMode.ModelSpace, ""),
                ("separados", OutputMode.SeparateFiles, ""),
            };
            // The project's own setup: with MergedViews off, the views must still end up in the merged drawing.
            if (setups.Count > 0) runs.Add(("layouts-config-projeto", OutputMode.MultipleLayouts, setups[0]));

            foreach (var (label, mode, setup) in runs)
                ExportSheets(doc, name, picked, label, mode, setup, Path.Combine(folder, name, label), log);

            // Views: the ones placed on the picked sheets, merged side by side in model space.
            var placedIds = picked.SelectMany(item => Placed(doc, (ViewSheet)doc.GetElement(item.Id))).Select(view => view.Id).ToHashSet();
            var views = SheetSource.Views(doc).Where(view => placedIds.Contains(view.Id)).Take(4).ToList();
            log.Check($"{name}: vistas para exportar", views.Count > 0, string.Join(", ", views.Select(view => view.Name)));
            if (views.Count > 0) ExportViews(doc, name, views, Path.Combine(folder, name, "vistas"), log);
        }
        finally
        {
            doc.Close(false);
        }
    }

    private static List<View> Placed(Document doc, ViewSheet sheet) =>
        sheet.GetAllPlacedViews().Select(id => doc.GetElement(id)).OfType<View>()
            .Where(view => view.ViewType is not ViewType.Schedule and not ViewType.Legend).ToList();

    private static void ExportSheets(Document doc, string model, IReadOnlyList<ExportItem> sheets, string label, OutputMode mode,
        string setup, string folder, Reporter log)
    {
        Directory.CreateDirectory(folder);
        var request = new ExportRequest(sheets, mode, false, NamePattern.NumberAndName, folder, setup, model);
        var plan = ExportService.Plan(request);
        var report = ExportService.Run(doc, request, plan);
        var tag = $"{model} {label}" + (setup.Length > 0 ? $" ('{setup}')" : "");
        log.Check($"{tag}: sem falhas", report.Failures.Count == 0, string.Join(" | ", report.Failures));
        log.Info($"{tag}: {report.Elapsed.TotalSeconds:0.#} s, perdas avisadas: {report.Losses.Count}");

        if (mode == OutputMode.SeparateFiles)
        {
            log.Check($"{tag}: um arquivo por folha", report.Written.Count(file => file.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) == sheets.Count);
            return;
        }
        if (report.MergedFile is null)
        {
            log.Check($"{tag}: desenho mesclado", false);
            return;
        }

        var summary = report.MergedSummary;
        var layouts = summary?.Layouts ?? [];
        log.Info($"{tag}: layouts {string.Join(", ", layouts)}; {summary?.Entities} entidades");
        log.Check($"{tag}: sem Layout1/Layout2 vazios", mode == OutputMode.ModelSpace || !layouts.Any(l => l is "Layout1" or "Layout2"));

        switch (mode)
        {
            case OutputMode.MultipleLayouts:
                log.Check($"{tag}: um layout por folha", layouts.Count == sheets.Count, string.Join(", ", layouts));
                foreach (var (sheet, layout) in sheets.Zip(layouts))
                {
                    var expected = ((ViewSheet)doc.GetElement(sheet.Id)).GetAllViewports().Count;
                    CheckLayout(report.MergedFile, layout, expected, tag, log);
                }
                break;
            case OutputMode.SingleLayout:
                log.Check($"{tag}: um layout so", layouts.Count == 1, string.Join(", ", layouts));
                if (layouts.Count == 1)
                {
                    var expected = sheets.Sum(sheet => ((ViewSheet)doc.GetElement(sheet.Id)).GetAllViewports().Count);
                    CheckLayout(report.MergedFile, layouts[0], expected, tag, log);
                }
                break;
        }

        CheckModelSpace(report.MergedFile, tag, log);
        foreach (var layout in layouts.Append("Model")) Picture(report.MergedFile, layout, folder);
    }

    private static void ExportViews(Document doc, string model, IReadOnlyList<ExportItem> views, string folder, Reporter log)
    {
        Directory.CreateDirectory(folder);
        var request = new ExportRequest(views, OutputMode.ModelSpace, false, NamePattern.NumberAndName, folder, "", model + " - vistas");
        var report = ExportService.Run(doc, request, ExportService.Plan(request));
        var tag = $"{model} vistas";
        log.Check($"{tag}: sem falhas", report.Failures.Count == 0, string.Join(" | ", report.Failures));
        log.Check($"{tag}: desenho mesclado", report.MergedFile is not null);
        if (report.MergedFile is null) return;
        CheckModelSpace(report.MergedFile, tag, log);
        Picture(report.MergedFile, "Model", folder);
    }

    /// <summary>
    /// The layout holds one viewport per Revit viewport of the sheet (legends included; the layout's own overall one is
    /// left behind) and more: the title block.
    /// </summary>
    private static void CheckLayout(string file, string layout, int expectedViewports, string tag, Reporter log)
    {
        var viewports = CadcraftBridge.Run(["run", file, "--cmd", "layout.set " + JsonSerializer.Serialize(new { name = layout }),
            "--cmd", "entities {\"type\":\"viewport\",\"limit\":100000}"]);
        var (overall, views) = CadcraftOutput.ParseViewports(viewports.Output);
        log.Check($"{tag} [{layout}]: viewports = vistas da folha", views.Count + (overall is null ? 0 : 1) == expectedViewports,
            $"{views.Count + (overall is null ? 0 : 1)} de {expectedViewports}");

        var all = CadcraftBridge.Run(["run", file, "--cmd", "layout.set " + JsonSerializer.Serialize(new { name = layout }),
            "--cmd", "entities {\"limit\":1}"]);
        var count = System.Text.RegularExpressions.Regex.Match(all.Output, "\"count\":(\\d+)");
        var entities = count.Success ? int.Parse(count.Groups[1].Value) : 0;
        log.Check($"{tag} [{layout}]: carimbo e anotacoes no papel", entities > expectedViewports, $"{entities} entidades");
    }

    private static void CheckModelSpace(string file, string tag, Reporter log)
    {
        var info = CadcraftBridge.Run(["info", file, "--compact"]);
        var extents = info.ExitCode == 0 ? CadcraftOutput.ParseExtents(info.Output) : null;
        log.Check($"{tag}: model space com as vistas", extents is not null, extents is { } box ? $"{box.X1 - box.X0:0.#} x {box.Y1 - box.Y0:0.#}" : "vazio");
    }

    private static void Picture(string file, string layout, string folder)
    {
        var png = Path.Combine(folder, FileNamer.Sanitize(layout) + ".png").Replace('\\', '/');
        var plot = layout == "Model"
            ? new Dictionary<string, object> { ["path"] = png, ["layout"] = "Model", ["device"] = "CADCraft PNG", ["plotArea"] = "extents", ["fit"] = true }
            : new Dictionary<string, object> { ["path"] = png, ["layout"] = layout, ["device"] = "CADCraft PNG" };
        CadcraftBridge.Run(["run", file, "--cmd", "plot " + JsonSerializer.Serialize(plot)]);
    }

    private sealed class Reporter(string path)
    {
        private int _passed;
        private int _failed;

        private void Write(string line) => File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");

        public void Info(string text) => Write("INFO   " + text);

        public void Check(string name, bool ok, string detail = "")
        {
            if (ok) _passed++; else _failed++;
            Write((ok ? "PASSOU " : "FALHOU ") + name + (detail.Length > 0 ? $"  [{detail}]" : ""));
        }

        public void Finish() => Write($"FIM passou={_passed} falhou={_failed}");
    }
}
