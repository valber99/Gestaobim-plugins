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

        // The empty Layout1 and Layout2 of a new drawing go once the sheets have their own layouts.
        Assert.Equal(expectedNewLayouts == 0 ? baseline.Layouts.Count : expectedNewLayouts, summary.Layouts.Count);
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

    private static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>
    /// A drawing shaped like a Revit sheet export: a view in model space (a 30 x 20 rectangle at <paramref name="modelX"/>)
    /// and, in a layout, an A3 title block whose lower-left corner is at -100,50 (as Revit writes sheet coordinates),
    /// with a viewport at 1:100 showing the view.
    /// </summary>
    private string RevitLikeSheet(string name, double modelX)
    {
        var path = Path.Combine(_folder, name);
        var (code, _, err) = Run(["run", "--metric",
            "--cmd", "rectang " + Json(new { p1 = new[] { modelX, 0.0 }, p2 = new[] { modelX + 30000, 20000.0 } }),
            "--cmd", "layout.new " + Json(new { name = "Folha", viewport = false }),
            "--cmd", "layout.set " + Json(new { name = "Folha" }),
            "--cmd", "rectang " + Json(new { p1 = new[] { -100.0, 50 }, p2 = new[] { 320.0, 347 } }),
            "--cmd", "text " + Json(new { at = new[] { 200.0, 60 }, text = "CARIMBO " + name, height = 5 }),
            "--cmd", "mview " + Json(new { p1 = new[] { -90.0, 100 }, p2 = new[] { 230.0, 330 } }),
            "--cmd", "viewport.set " + Json(new { handle = "{last}", scale = 0.01, center = new[] { modelX + 15000, 10000.0 } }),
            "--save", path]);
        Assert.True(code == 0 && File.Exists(path), err);
        return path;
    }

    private SheetExtent RevitLikeExtent(string name, string file)
    {
        var extent = Extent(name, file);
        var layouts = Run(["run", file, "--cmd", "getvar {\"name\":\"INSUNITS\"}", "--cmd", "layout.list {}"]);
        var sheet = CadcraftOutput.ParseSheetLayout(layouts.Out);
        Assert.True(sheet is not null, layouts.Err);
        var viewports = Run(["run", file, "--cmd", "layout.set " + Json(new { name = sheet!.Value.Layout }),
            "--cmd", "entities {\"type\":\"viewport\",\"limit\":1000}"]);
        var (overall, views) = CadcraftOutput.ParseViewports(viewports.Out);
        Assert.Single(views);
        return extent with
        {
            PaperWidthMm = 420, PaperHeightMm = 297, OriginXMm = -100, OriginYMm = 50,
            Space = new PaperSpace(sheet.Value.Layout, sheet.Value.InsUnits, overall, views),
        };
    }

    [Fact]
    public void EachSheetKeepsItsOwnPaperSpaceAndItsViewportFollowsTheMovedView()
    {
        if (_cli is null) return;

        var one = RevitLikeExtent("Planta", RevitLikeSheet("planta.dwg", 0));
        var two = RevitLikeExtent("Corte", RevitLikeSheet("corte.dwg", 0));
        var output = Path.Combine(_folder, "folhas.dwg");

        foreach (var run in MergePlanner.PlanRuns([one, two], new MergeOptions(OutputMode.MultipleLayouts), output, maxLength: 1500))
        {
            var (code, _, err) = Run(run);
            Assert.True(code == 0, err);
        }

        var summary = CadcraftOutput.ParseSummary(Run(["info", output, "--compact"]).Out)!;
        Assert.Equal(["Planta", "Corte"], summary.Layouts);

        // The second sheet's view was moved right in model space; its viewport must look at the new place.
        var placed = MergePlanner.Place([one, two]);
        var (_, views) = CadcraftOutput.ParseViewports(Run(["run", output, "--cmd", "layout.set {\"name\":\"Corte\"}",
            "--cmd", "entities {\"type\":\"viewport\",\"limit\":1000}"]).Out);
        var view = Assert.Single(views);
        Assert.Equal(15000 + placed[1].OffsetX, view.X, 3);
        Assert.Equal(10000 + placed[1].OffsetY, view.Y, 3);

        // The title block was copied with its lower-left corner at 0,0 of the layout.
        var texts = Run(["run", output, "--cmd", "layout.set {\"name\":\"Corte\"}", "--cmd", "entities {\"type\":\"text\",\"limit\":10}"]).Out;
        Assert.Contains("CARIMBO corte.dwg", texts);
        Assert.Contains("\"x\":300", texts);
    }
}
