using System.Diagnostics;
using System.Text;
using Gestaobim.DwgExport.Core;
using Xunit;

namespace GBDwgExport.Core.Tests;

/// <summary>
/// Runs the real, unmodified cadcraft-cli with the plans MergePlanner builds. They need the tool, which the installer
/// puts in %APPDATA%\GestaoBIM\Tools\cadcraft (or GB_CADCRAFT_CLI points to). Without it each test returns at once,
/// so a machine that has no CLI (such as a plain Linux runner) still passes; the CI package job fetches the CLI.
/// </summary>
public class CadcraftIntegrationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gbdwg-int-" + Guid.NewGuid().ToString("N"));
    private readonly string? _cli = DwgPaths.FindCadcraft();

    public CadcraftIntegrationTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private (int Code, string Out, string Err) Run(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(_cli!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "cadcraft-cli demorou demais");
        return (process.ExitCode, output.Result, error.Result);
    }

    private string Sample(string name)
    {
        var path = Path.Combine(_folder, name);
        var (code, _, err) = Run(["run", "--sample", "--save", path]);
        Assert.True(code == 0 && File.Exists(path), err);
        return path;
    }

    private SheetExtent Extent(string name, string file)
    {
        var (code, output, err) = Run(["info", file, "--compact"]);
        Assert.Equal(0, code);
        var box = CadcraftOutput.ParseExtents(output);
        Assert.True(box is not null, "limites do desenho: " + err);
        var (x0, y0, x1, y1) = box!.Value;
        return new SheetExtent(name, file, x0, y0, x1, y1);
    }

    [Theory]
    [InlineData(OutputMode.MultipleLayouts, 2)]
    [InlineData(OutputMode.SingleLayout, 1)]
    [InlineData(OutputMode.ModelSpace, 0)]
    public void MergePlansRunAndMakeTheExpectedLayouts(OutputMode mode, int expectedNewLayouts)
    {
        if (_cli is null) return;

        var one = Extent("Folha 1", Sample("a.dxf"));
        var two = Extent("Folha 2", Sample("b.dxf"));
        var output = Path.Combine(_folder, "merged.dwg");

        var plan = MergePlanner.Plan([one, two], new MergeOptions(mode, "A3"), output);
        var (code, _, err) = Run(plan);

        Assert.True(code == 0, err);
        Assert.True(File.Exists(output));
        var (infoCode, infoJson, _) = Run(["info", output, "--compact"]);
        Assert.Equal(0, infoCode);
        var summary = CadcraftOutput.ParseSummary(infoJson)!;
        var baseline = CadcraftOutput.ParseSummary(Run(["info", one.File, "--compact"]).Out)!;

        Assert.Equal(baseline.Layouts.Count + expectedNewLayouts, summary.Layouts.Count);
        // Two sheets were imported, so the merged drawing holds more than one of them.
        Assert.True(summary.Entities > baseline.Entities);
    }

    [Fact]
    public void EachLayoutPageIsThePaperOfItsSheet()
    {
        if (_cli is null) return;

        var landscape = Extent("Paisagem", Sample("a.dxf")) with { PaperWidthMm = 594, PaperHeightMm = 420 };
        var portrait = Extent("Retrato", Sample("b.dxf")) with { PaperWidthMm = 420, PaperHeightMm = 594 };
        var output = Path.Combine(_folder, "paper.dwg");
        var (code, _, err) = Run(MergePlanner.Plan([landscape, portrait], new MergeOptions(OutputMode.MultipleLayouts), output));
        Assert.True(code == 0, err);

        foreach (var (layout, wide, tall) in new[] { ("Paisagem", 594.0, 420.0), ("Retrato", 420.0, 594.0) })
        {
            var pdf = Path.Combine(_folder, layout + ".pdf");
            var (pdfCode, _, pdfErr) = Run(["run", output, "--cmd", "layout.set " + System.Text.Json.JsonSerializer.Serialize(new { name = layout }),
                "--cmd", "exportpdf " + System.Text.Json.JsonSerializer.Serialize(new { path = pdf.Replace('\\', '/') })]);
            Assert.True(pdfCode == 0 && File.Exists(pdf), pdfErr);

            // PDF points: 1 mm = 72 / 25.4 points.
            var box = System.Text.RegularExpressions.Regex.Match(Encoding.Latin1.GetString(File.ReadAllBytes(pdf)),
                @"/MediaBox\s*\[\s*0\s+0\s+([0-9.]+)\s+([0-9.]+)\s*\]");
            Assert.True(box.Success, "MediaBox nao encontrado em " + layout);
            var widthMm = double.Parse(box.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 25.4 / 72;
            var heightMm = double.Parse(box.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) * 25.4 / 72;
            Assert.InRange(widthMm, wide - 2, wide + 2);
            Assert.InRange(heightMm, tall - 2, tall + 2);
        }
    }

    [Fact]
    public void LayoutsAreNamedAfterTheSheets()
    {
        if (_cli is null) return;

        var one = Extent("Planta Térrea", Sample("a.dxf"));
        var two = Extent("Corte AA", Sample("b.dxf"));
        var output = Path.Combine(_folder, "named.dwg");

        var (code, _, err) = Run(MergePlanner.Plan([one, two], new MergeOptions(OutputMode.MultipleLayouts), output));
        Assert.True(code == 0, err);

        var summary = CadcraftOutput.ParseSummary(Run(["info", output, "--compact"]).Out)!;
        Assert.Contains("Planta Térrea", summary.Layouts);
        Assert.Contains("Corte AA", summary.Layouts);
    }
}
