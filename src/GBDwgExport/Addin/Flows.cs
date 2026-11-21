using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using Autodesk.Revit.UI;
using Gestaobim.DwgExport.Core;
using Gestaobim.DwgExport.Revit;
using Gestaobim.DwgExport.UI;

namespace Gestaobim.DwgExport.Addin;

internal static class Flows
{
    public static void Export(UIApplication app, OutputMode mode, bool views)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            Dialogs.Alert("Abra um projeto do Revit para exportar.");
            return;
        }

        var settings = ExportSettings.Load();
        settings.Mode = mode;
        var window = new ExportWindow(doc, settings, views);
        if (window.ShowDialog() != true || window.Request is not { } request) return;

        var plan = ExportService.Plan(request);
        if (plan.PathsTooLong.Count > 0)
        {
            // The Windows path limit: offer the Desktop, in a folder named after the project, before giving up.
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FileNamer.Sanitize(request.ProjectName));
            var fallback = request with { Folder = desktop };
            var fallbackPlan = ExportService.Plan(fallback);
            if (fallbackPlan.PathsTooLong.Count == 0 && Dialogs.Alert(
                    $"O caminho completo de {plan.PathsTooLong.Count} arquivo(s) passa de {FileNamer.MaxPath} caracteres.\n\n" +
                    $"Salvar na Área de Trabalho, na pasta:\n{desktop}?", ask: true))
            {
                request = fallback;
                plan = fallbackPlan;
            }
            else
            {
                Dialogs.Alert($"O caminho completo destes arquivos passa de {FileNamer.MaxPath} caracteres:\n\n" +
                              string.Join("\n", plan.PathsTooLong.Take(10)) +
                              "\n\nEscolha uma pasta mais curta ou renomeie as folhas.");
                return;
            }
        }

        if (plan.Existing.Count > 0 && !Dialogs.Alert(
                $"{plan.Existing.Count} arquivo(s) já existem na pasta:\n\n{string.Join("\n", plan.Existing.Take(8).Select(Path.GetFileName))}" +
                (plan.Existing.Count > 8 ? "\n..." : "") + "\n\nDeseja sobrescrever?", ask: true))
            return;

        ExportReport report;
        var previous = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        var progress = new ProgressWindow();
        progress.Show();
        try
        {
            report = ExportService.Run(doc, request, plan, progress.Update);
        }
        finally
        {
            progress.Close();
            Mouse.OverrideCursor = previous;
        }

        Dialogs.Alert(Summarize(report, request), "GB DWG Export");
        if (request.Folder.Length > 0 && Directory.Exists(request.Folder) && report.Written.Count > 0 && ExportSettings.Load().OpenFolder)
            Open(report.MergedFile ?? report.Written[0]);
    }

    public static string Summarize(ExportReport report, ExportRequest request)
    {
        var text = new StringBuilder();
        var merged = report.MergedFile is not null;
        text.AppendLine(merged
            ? $"Desenho criado: {Path.GetFileName(report.MergedFile)}"
            : $"{report.Written.Count} arquivo(s) exportado(s).");
        if (report.MergedSummary is { } summary)
            text.AppendLine($"{summary.Layouts.Count} layout(s), {summary.Layers} camada(s), {summary.Entities} entidade(s).");
        text.AppendLine($"Tempo: {report.Elapsed.TotalSeconds:0.#} s");

        if (report.Losses.Count > 0)
        {
            text.AppendLine().AppendLine("A mesclagem avisou que não conseguiu manter este conteúdo:");
            foreach (var loss in report.Losses.Take(6)) text.AppendLine("  • " + loss);
            text.AppendLine("Confira o desenho no AutoCAD antes de entregar. Para ter tudo, exporte com \"Um arquivo por folha\".");
        }
        if (report.Failures.Count > 0)
        {
            text.AppendLine().AppendLine($"{report.Failures.Count} falha(s):");
            foreach (var failure in report.Failures.Take(8)) text.AppendLine("  • " + failure);
        }
        return text.ToString().TrimEnd();
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening the folder is a convenience; the export already succeeded.
        }
    }

    public static void SaveSettings()
    {
        var file = Dialogs.PickFile(save: true, "Configurações GB DWG Export (*.zip)|*.zip", "Exportar configurações", "GBDwgExport-configuracoes.zip");
        if (file is null) return;
        SettingsTransfer.Export(ExportSettings.Load(), file, typeof(Flows).Assembly.GetName().Version?.ToString(3) ?? "0");
        Dialogs.Alert($"Configurações salvas em:\n{file}");
    }

    public static void LoadSettings()
    {
        var file = Dialogs.PickFile(save: false, "Configurações GB DWG Export (*.zip)|*.zip", "Importar configurações");
        if (file is null) return;
        try
        {
            SettingsTransfer.Import(file).Save();
            Dialogs.Alert("Configurações importadas. Elas valem na próxima vez que você abrir uma janela de exportação.");
        }
        catch (TransferException error)
        {
            Dialogs.Alert(error.Message);
        }
    }
}
