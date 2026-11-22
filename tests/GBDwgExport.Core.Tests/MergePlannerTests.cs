using Gestaobim.DwgExport.Core;
using Xunit;

namespace GBDwgExport.Core.Tests;

public class MergePlannerTests
{
    private static readonly SheetExtent A = new("A101", @"C:\tmp\A101.dwg", -100, -50, 900, 450);
    private static readonly SheetExtent B = new("A102 - Corte", @"C:\tmp\A102.dwg", 0, 0, 400, 200);

    [Fact]
    public void SheetsAreSideBySideWithAGapAndNeverOverlap()
    {
        var placed = MergePlanner.Place([A, B]);

        Assert.Equal(0, placed[0].X0);
        Assert.Equal(0, placed[0].Y0);
        Assert.Equal(1000, placed[0].X1);
        Assert.Equal(500, placed[0].Y1);
        Assert.Equal(100, placed[0].OffsetX);   // sheet content starting at -100 lands at 0
        Assert.Equal(50, placed[0].OffsetY);

        var gap = 1000 * MergePlanner.GapFraction;
        Assert.Equal(1000 + gap, placed[1].X0);
        Assert.True(placed[1].X0 > placed[0].X1);
        Assert.Equal(400, placed[1].X1 - placed[1].X0);
    }

    [Fact]
    public void MultipleLayoutsMakeOneLayoutPerSheetInOrder()
    {
        var args = MergePlanner.Plan([A, B], new MergeOptions(OutputMode.MultipleLayouts), @"C:\out\all.dwg");

        Assert.Equal(["run", "--metric"], args.Take(2));
        Assert.Equal(["--save", "C:/out/all.dwg"], args.TakeLast(2));
        var commands = Commands(args);
        Assert.Equal(2, commands.Count(c => c.StartsWith("import ")));
        Assert.Equal(2, commands.Count(c => c.StartsWith("layout.new ")));
        Assert.Contains(commands, c => c.StartsWith("layout.new ") && c.Contains("\"A101\""));
        Assert.Contains(commands, c => c.StartsWith("layout.new ") && c.Contains("A102 - Corte"));
        Assert.Equal(2, commands.Count(c => c.StartsWith("pagesetup ") && c.Contains("\"paper\":\"A1\"")));
        Assert.Equal(2, commands.Count(c => c.StartsWith("mview ")));
        Assert.Equal(2, commands.Count(c => c.StartsWith("zoom ")));

        // Every import comes before the first layout, so all content exists when the viewports are framed.
        Assert.True(commands.FindLastIndex(c => c.StartsWith("import ")) < commands.FindIndex(c => c.StartsWith("layout.new ")));
    }

    [Fact]
    public void ImportPathsUseForwardSlashes()
    {
        var args = MergePlanner.Plan([A], new MergeOptions(OutputMode.MultipleLayouts), @"C:\out\all.dwg");
        Assert.Contains(Commands(args), c => c.StartsWith("import ") && c.Contains("C:/tmp/A101.dwg"));
    }

    [Fact]
    public void ModelSpaceOnlyImports()
    {
        var args = MergePlanner.Plan([A, B], new MergeOptions(OutputMode.ModelSpace), @"C:\out\all.dwg");
        var commands = Commands(args);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.StartsWith("import ", c));
    }

    [Fact]
    public void SingleLayoutFramesEverySheetInOneViewport()
    {
        var args = MergePlanner.Plan([A, B], new MergeOptions(OutputMode.SingleLayout, "A0"), @"C:\out\all.dwg");
        var commands = Commands(args);

        Assert.Single(commands, c => c.StartsWith("layout.new "));
        Assert.Single(commands, c => c.StartsWith("zoom "));
        var page = commands.Single(c => c.StartsWith("pagesetup "));
        Assert.Contains("\"width\":1189", page);   // the long side keeps the chosen paper
        Assert.Contains("\"height\":", page);
    }

    [Fact]
    public void ZoomWindowCoversTheSheetWithAMargin()
    {
        var args = MergePlanner.Plan([A], new MergeOptions(OutputMode.MultipleLayouts), @"C:\out\all.dwg");
        var zoom = Commands(args).Single(c => c.StartsWith("zoom "));

        // Sheet is placed at 0,0 - 1000,500; the 2% margin makes the window -20,-10 - 1020,510.
        Assert.Contains("-20", zoom);
        Assert.Contains("1020", zoom);
        Assert.Contains("510", zoom);
    }

    [Fact]
    public void LayoutNamesAreMadeValidAndUnique()
    {
        var one = new SheetExtent("Corte A/B: 1", "a.dwg", 0, 0, 10, 10);
        var two = new SheetExtent("corte a/b: 1", "b.dwg", 0, 0, 10, 10);
        var args = MergePlanner.Plan([one, two], new MergeOptions(OutputMode.MultipleLayouts), "o.dwg");
        var names = Commands(args).Where(c => c.StartsWith("layout.new ")).ToList();

        Assert.Contains("Corte A_B_ 1", names[0]);
        Assert.Contains("corte a_b_ 1_2", names[1]);
        Assert.Equal("Layout", MergePlanner.LayoutName("   "));
        Assert.Equal(255, MergePlanner.LayoutName(new string('x', 400)).Length);
    }

    [Fact]
    public void ALayoutHasExactlyThePaperTheSheetUsesInRevit()
    {
        var a1 = new SheetExtent("A101", "a.dwg", 0, 0, 841, 594, PaperWidthMm: 841, PaperHeightMm: 594);
        var a3Portrait = new SheetExtent("A102", "b.dwg", 0, 0, 297, 420, PaperWidthMm: 297, PaperHeightMm: 420);
        // The options ask for A4 portrait, but a sheet with a known paper never uses the fallback.
        var args = MergePlanner.Plan([a1, a3Portrait], new MergeOptions(OutputMode.MultipleLayouts, "A4", false), "o.dwg");
        var commands = Commands(args);

        var pages = commands.Where(c => c.StartsWith("pagesetup ")).ToList();
        Assert.Contains("\"width\":841", pages[0]);
        Assert.Contains("\"height\":594", pages[0]);
        Assert.Contains("\"landscape\":true", pages[0]);
        Assert.Contains("\"width\":297", pages[1]);
        Assert.Contains("\"height\":420", pages[1]);
        Assert.Contains("\"landscape\":false", pages[1]);
        Assert.DoesNotContain(pages, page => page.Contains("\"paper\""));
    }

    [Fact]
    public void AViewportFillsThePaperEdgeToEdgeAndFramesTheSheetExactly()
    {
        var sheet = new SheetExtent("A101", "a.dwg", 10, 20, 851, 614, PaperWidthMm: 841, PaperHeightMm: 594);
        var args = MergePlanner.Plan([sheet], new MergeOptions(OutputMode.MultipleLayouts), "o.dwg");
        var commands = Commands(args);

        var view = commands.Single(c => c.StartsWith("mview "));
        Assert.Contains("\"p1\":[0,0]", view);
        Assert.Contains("\"p2\":[841,594]", view);

        // The sheet lands at 0,0 - 841,594 and the window is that rectangle, with no margin.
        var zoom = commands.Single(c => c.StartsWith("zoom "));
        Assert.Contains("\"p1\":[0,0]", zoom);
        Assert.Contains("\"p2\":[841,594]", zoom);
    }

    [Theory]
    [InlineData(840, 594, 841, 594)]      // A1 as Revit reports it
    [InlineData(1188, 840, 1189, 841)]    // A0
    [InlineData(420, 297, 420, 297)]      // A3 exact
    [InlineData(297, 420, 297, 420)]      // A3 portrait stays portrait
    [InlineData(594, 840, 594, 841)]      // A1 portrait, a millimetre short
    [InlineData(500, 300, 500, 300)]      // not a standard paper: kept as measured
    [InlineData(843, 594, 841, 594)]      // 2 mm over: still within the tolerance
    [InlineData(846, 594, 846, 594)]      // 5 mm over: too far, kept as measured
    public void MeasuredPapersSnapToTheStandardOnesWithinTolerance(double w, double h, double expectedW, double expectedH)
    {
        var (width, height) = MergePlanner.SnapToStandardPaper(w, h);
        Assert.Equal((expectedW, expectedH), (width, height));
    }

    [Fact]
    public void ASheetWithoutKnownPaperFallsBackToTheOptionsPaper()
    {
        var sheet = new SheetExtent("A101", "a.dwg", 0, 0, 100, 50);
        var args = MergePlanner.Plan([sheet], new MergeOptions(OutputMode.MultipleLayouts, "A3"), "o.dwg");

        Assert.Contains(Commands(args), c => c.StartsWith("pagesetup ") && c.Contains("\"paper\":\"A3\""));
    }

    [Fact]
    public void SingleLayoutFollowsTheLargestSheetPaper()
    {
        var small = new SheetExtent("A", "a.dwg", 0, 0, 420, 297, PaperWidthMm: 420, PaperHeightMm: 297);
        var large = new SheetExtent("B", "b.dwg", 0, 0, 1189, 841, PaperWidthMm: 1189, PaperHeightMm: 841);
        var args = MergePlanner.Plan([small, large], new MergeOptions(OutputMode.SingleLayout), "o.dwg");

        // The long side is the A0 one, whatever the fallback paper says.
        Assert.Contains(Commands(args), c => c.StartsWith("pagesetup ") && c.Contains("\"width\":1189"));
    }

    [Theory]
    [InlineData("Model", "Model_2")]
    [InlineData("model", "model_2")]
    [InlineData("Layout1", "Layout1_2")]
    [InlineData("LAYOUT2", "LAYOUT2_2")]
    public void ReservedLayoutNamesGetASuffix(string sheetName, string expected)
    {
        var sheet = new SheetExtent(sheetName, "a.dwg", 0, 0, 10, 10);
        var args = MergePlanner.Plan([sheet], new MergeOptions(OutputMode.MultipleLayouts), "o.dwg");

        Assert.Contains(Commands(args), c => c.StartsWith("layout.new ") && c.Contains($"\"{expected}\""));
    }

    [Fact]
    public void SingleLayoutNameAlsoAvoidsReservedNames()
    {
        var args = MergePlanner.Plan([A], new MergeOptions(OutputMode.SingleLayout, LayoutName: "Model"), "o.dwg");

        Assert.Contains(Commands(args), c => c.StartsWith("layout.new ") && c.Contains("\"Model_2\""));
    }

    [Fact]
    public void PortraitSwapsThePaperSides()
    {
        var args = MergePlanner.Plan([A], new MergeOptions(OutputMode.MultipleLayouts, "A3", Landscape: false), "o.dwg");
        var view = Commands(args).Single(c => c.StartsWith("mview "));

        // A3 portrait is 297 x 420: the viewport spans the page minus a 10 mm margin.
        Assert.Contains("287", view);
        Assert.Contains("410", view);
    }

    [Fact]
    public void NothingToMergeIsRejected()
    {
        Assert.Throws<ArgumentException>(() => MergePlanner.Plan([], new MergeOptions(OutputMode.MultipleLayouts), "o.dwg"));
        Assert.Throws<ArgumentException>(() => MergePlanner.Plan([A], new MergeOptions(OutputMode.SeparateFiles), "o.dwg"));
    }

    private static SheetExtent WithPaperSpace(string name, string file, double x0, double x1) =>
        new(name, file, x0, 0, x1, 20, PaperWidthMm: 841, PaperHeightMm: 594, OriginXMm: -216.5, OriginYMm: 94.2,
            Space: new PaperSpace("Layout1", 6, "560A", [new ViewportCenter("5608", 2.5, 8.2), new ViewportCenter("560D", 36.8, 8.1)]));

    [Fact]
    public void ASheetWithItsPaperSpaceIsCopiedNotRebuilt()
    {
        var one = WithPaperSpace("P. 02", @"C:\tmp\p02.dwg", -12, 52);
        var two = WithPaperSpace("P. 03", @"C:\tmp\p03.dwg", -15, 95);
        var commands = Commands(MergePlanner.Plan([one, two], new MergeOptions(OutputMode.MultipleLayouts), "o.dwg"));

        // The drawing takes the units Revit exported in (6 = metres).
        Assert.Equal("units {\"insunits\":6}", commands[0]);
        Assert.Equal(2, commands.Count(c => c.StartsWith("open ")));
        Assert.Equal(2, commands.Count(c => c.StartsWith("close ")));
        // The layout's own overall viewport is erased in the source, never copied.
        Assert.Equal(2, commands.Count(c => c == "erase {\"handles\":[\"560A\"]}"));
        Assert.DoesNotContain(commands, c => c.StartsWith("mview ") || c.StartsWith("zoom "));

        // The base point is the sheet's lower-left corner, in metres.
        Assert.Contains(commands, c => c.StartsWith("copybase ") && c.Contains("[-0.2165,0.0942]"));

        // The second sheet's viewports follow its views, moved right in model space.
        var placed = MergePlanner.Place([one, two]);
        var expected = 36.8 + placed[1].OffsetX;
        Assert.Contains(commands, c => c.StartsWith("viewport.set ") && c.Contains("\"560D\"")
                                       && c.Contains(expected.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // The page is the sheet's paper, printed so one metre of paper space is 1000 mm.
        var page = commands.First(c => c.StartsWith("pagesetup "));
        Assert.Contains("\"width\":841", page);
        Assert.Contains("\"height\":594", page);
        Assert.Contains("\"scale\":1000", page);

        // The empty layouts of a new drawing are removed at the end.
        Assert.Equal("layout.delete {\"name\":\"Layout2\"}", commands[^1]);
    }

    [Fact]
    public void SingleLayoutPutsTheSheetsSideBySideOnOnePage()
    {
        var one = WithPaperSpace("P. 02", "a.dwg", 0, 10);
        var two = WithPaperSpace("P. 03", "b.dwg", 0, 10);
        var commands = Commands(MergePlanner.Plan([one, two], new MergeOptions(OutputMode.SingleLayout), "o.dwg"));

        Assert.Single(commands, c => c.StartsWith("layout.new "));
        var pastes = commands.Where(c => c.StartsWith("pasteclip ")).ToList();
        Assert.Equal("pasteclip {\"at\":[0,0]}", pastes[0]);
        // The second sheet starts one paper width plus the gap to the right, in metres.
        Assert.Equal("pasteclip {\"at\":[0.891,0]}", pastes[1]);
        Assert.Contains(commands, c => c.StartsWith("pagesetup ") && c.Contains("\"width\":1732") && c.Contains("\"height\":594"));
    }

    [Fact]
    public void ALongMergeIsSplitIntoRunsThatEachFitTheCommandLine()
    {
        var sheets = Enumerable.Range(1, 40).Select(i => WithPaperSpace($"Folha {i}", $@"C:\tmp\folha{i}.dwg", 0, 10)).ToList();
        var runs = MergePlanner.PlanRuns(sheets, new MergeOptions(OutputMode.MultipleLayouts), @"C:\out\all.dwg", maxLength: 4000);

        Assert.True(runs.Count > 1);
        Assert.Equal(["run", "--metric"], runs[0].Take(2));
        foreach (var run in runs.Skip(1)) Assert.Equal(["run", "C:/out/all.dwg", "--in-place"], run.Take(3));
        foreach (var run in runs)
        {
            Assert.Equal(["--save", "C:/out/all.dwg"], run.TakeLast(2));
            Assert.True(run.Sum(arg => arg.Length + 3) <= 4000);
            // A sheet's copy never spans two runs: every open has its paste in the same run.
            var commands = Commands(run);
            Assert.Equal(commands.Count(c => c.StartsWith("open ")), commands.Count(c => c.StartsWith("pasteclip ")));
        }

        // Together the runs do exactly what the single plan does.
        var single = Commands(MergePlanner.Plan(sheets, new MergeOptions(OutputMode.MultipleLayouts), @"C:\out\all.dwg"));
        Assert.Equal(single, runs.SelectMany(Commands));
    }

    [Fact]
    public void TheSheetLayoutIsTheOneWithViewports()
    {
        const string output = """
            {"name":"INSUNITS","value":6}
            [{"current":false,"name":"Layout1","tabOrder":1,"viewports":["5608","560D"]},{"current":false,"name":"Layout2","tabOrder":2,"viewports":[]}]
            """;
        Assert.Equal(("Layout1", 6), CadcraftOutput.ParseSheetLayout(output));
        Assert.Null(CadcraftOutput.ParseSheetLayout("[{\"name\":\"Layout1\",\"viewports\":[]}]"));
    }

    [Fact]
    public void ViewportsAreReadAndTheOverallOneIsSetApart()
    {
        const string output = """
            {"name":"Layout1"}
            {"count":3,"entities":[{"geometry":{"id":2,"viewCenter":{"x":2.5,"y":8.2}},"handle":"5608","type":"Viewport"},{"geometry":{"id":1,"viewCenter":{"x":6.0,"y":4.5}},"handle":"560A","type":"Viewport"},{"geometry":{"id":3,"viewCenter":{"x":36.8,"y":8.1}},"handle":"560D","type":"Viewport"}],"offset":0,"returned":3}
            """;
        var (overall, views) = CadcraftOutput.ParseViewports(output);

        Assert.Equal("560A", overall);
        Assert.Equal([new ViewportCenter("5608", 2.5, 8.2), new ViewportCenter("560D", 36.8, 8.1)], views);
    }

    [Theory]
    [InlineData(6, 1000)]
    [InlineData(4, 1)]
    [InlineData(5, 10)]
    [InlineData(1, 25.4)]
    [InlineData(0, 1)]
    public void DrawingUnitsBecomeMillimetres(int insUnits, double mm) => Assert.Equal(mm, MergePlanner.MillimetersPerUnit(insUnits));

    private static List<string> Commands(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "--cmd") result.Add(args[i + 1]);
        return result;
    }
}
