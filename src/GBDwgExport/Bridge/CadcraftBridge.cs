using System.Diagnostics;
using System.Text;
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
            "O motor de mesclagem (cadcraft-cli) nao foi encontrado. Rode o instalador do GB DWG Export de novo.");

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

    /// <summary>Merges the exported sheets (name, file) into <paramref name="output"/> and reports what the tool could not keep.</summary>
    public static MergeResult Merge(IReadOnlyList<(string Name, string File)> sheets, MergeOptions options, string output)
    {
        var extents = new List<SheetExtent>();
        foreach (var (name, file) in sheets)
        {
            var (x0, y0, x1, y1) = ReadExtents(file);
            extents.Add(new SheetExtent(name, file, x0, y0, x1, y1));
        }

        var folder = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var plan = MergePlanner.Plan(extents, options, output);
        var result = Run(plan);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new BridgeException($"A mesclagem falhou: {FirstLine(result.Error)}");

        var info = Run(["info", output, "--compact"]);
        var summary = info.ExitCode == 0 ? CadcraftOutput.ParseSummary(info.Output) : null;
        return new MergeResult(output, CadcraftOutput.ParseLosses(result.Error), summary);
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "(sem detalhes)";
}
