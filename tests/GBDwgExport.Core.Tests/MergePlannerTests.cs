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

    private static List<string> Commands(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "--cmd") result.Add(args[i + 1]);
        return result;
    }
}
