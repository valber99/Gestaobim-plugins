using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gestaobim.DwgExport.Core;

namespace Gestaobim.DwgExport.Bridge;

public sealed class BridgeException(string message) : Exception(message);

/// <summary>
/// Runs the unmodified cadcraft-cli that the installer puts next to the add-in. It stands in for AutoCAD when
/// several exported sheets are merged into one drawing. It never touches Revit: it only reads and writes files.
/// </summary>
public static class CadcraftBridge
{
    public sealed record RunResult(int ExitCode, string Output, string Error);

    private const int DefaultTimeoutMs = 10 * 60 * 1000;

    public static string? Find() => DwgPaths.FindCadcraft(Path.GetDirectoryName(typeof(CadcraftBridge).Assembly.Location));

    public static string Require() =>
        Find() ?? throw new BridgeException(
            "O motor de mesclagem (cadcraft-cli) não foi encontrado. Rode o instalador do Gestão BIM de novo.");

    public static RunResult Run(IEnumerable<string> arguments, int timeoutMs = DefaultTimeoutMs)
    {
        var info = new ProcessStartInfo(Require())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new BridgeException("Nao foi possivel iniciar o cadcraft-cli.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new BridgeException("A mesclagem demorou demais e foi interrompida.");
        }
        process.WaitForExit();
        return new RunResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    /// <summary>The rectangle a drawing's content occupies, so sheets can be placed without overlapping.</summary>
    public static (double X0, double Y0, double X1, double Y1) ReadExtents(string file)
    {
        var result = Run(["info", file, "--compact"]);
        if (result.ExitCode != 0)
            throw new BridgeException($"Nao foi possivel ler '{Path.GetFileName(file)}': {FirstLine(result.Error)}");
        return CadcraftOutput.ParseExtents(result.Output)
               ?? throw new BridgeException($"'{Path.GetFileName(file)}' esta vazio ou sem limites validos.");
    }

    public sealed record MergeResult(string File, IReadOnlyList<string> Losses, CadcraftOutput.Summary? Summary);

    /// <summary>
    /// A sheet or view to merge: its label, its exported file, the paper the sheet uses in Revit and the sheet's
    /// lower-left corner in sheet coordinates (both in millimetres, when known). A view has no paper.
    /// </summary>
    public sealed record MergeInput(string Name, string File, double? PaperWidthMm, double? PaperHeightMm,
        double? OriginXMm = null, double? OriginYMm = null, bool IsSheet = true);

    /// <summary>Merges the exported sheets or views into <paramref name="output"/> and reports what the tool could not keep.</summary>
    public static MergeResult Merge(IReadOnlyList<MergeInput> sheets, MergeOptions options, string output)
    {
        var layouts = options.Mode is OutputMode.MultipleLayouts or OutputMode.SingleLayout;
        var extents = new List<SheetExtent>();
        foreach (var sheet in sheets)
        {
            var (x0, y0, x1, y1) = ReadExtents(sheet.File);
            var space = layouts && sheet.IsSheet ? ReadPaperSpace(sheet.File) : null;
            extents.Add(new SheetExtent(sheet.Name, sheet.File, x0, y0, x1, y1, sheet.PaperWidthMm, sheet.PaperHeightMm,
                sheet.OriginXMm, sheet.OriginYMm, space));
        }

        var folder = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        if (File.Exists(output)) File.Delete(output);

        var errors = new StringBuilder();
        foreach (var run in MergePlanner.PlanRuns(extents, options, output))
        {
            var result = Run(run);
            errors.AppendLine(result.Error);
            if (result.ExitCode != 0 || !File.Exists(output))
                throw new BridgeException($"A mesclagem falhou: {FirstLine(result.Error)}");
        }

        var info = Run(["info", output, "--compact"]);
        var summary = info.ExitCode == 0 ? CadcraftOutput.ParseSummary(info.Output) : null;
        return new MergeResult(output, CadcraftOutput.ParseLosses(errors.ToString()).Distinct().ToList(), summary);
    }

    /// <summary>
    /// The paper space Revit wrote for a sheet: the layout with its viewports, and the drawing units. Null when the file
    /// has no layout with a viewport, so the sheet falls back to a layout framing its model space.
    /// </summary>
    public static PaperSpace? ReadPaperSpace(string file)
    {
        var layouts = Run(["run", file, "--cmd", "getvar {\"name\":\"INSUNITS\"}", "--cmd", "layout.list {}"]);
        if (layouts.ExitCode != 0 || CadcraftOutput.ParseSheetLayout(layouts.Output) is not { } sheet) return null;

        var layoutSet = "layout.set " + JsonSerializer.Serialize(new { name = sheet.Layout });
        var viewports = Run(["run", file, "--cmd", layoutSet, "--cmd", "entities {\"type\":\"viewport\",\"limit\":100000}"]);
        if (viewports.ExitCode != 0) return null;
        var (overall, views) = CadcraftOutput.ParseViewports(viewports.Output);
        return views.Count == 0 ? null : new PaperSpace(sheet.Layout, sheet.InsUnits, overall, views);
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "(sem detalhes)";
}
