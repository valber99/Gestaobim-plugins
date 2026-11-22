using System.Text.Json;

namespace Gestaobim.DwgExport.Core;

public enum OutputMode
{
    /// <summary>One file per sheet. No post-processing.</summary>
    SeparateFiles,

    /// <summary>One drawing: each sheet's own paper space becomes a layout, and every view sits side by side in model space.</summary>
    MultipleLayouts,

    /// <summary>One drawing, every view side by side in model space, no layouts.</summary>
    ModelSpace,

    /// <summary>One drawing: every sheet side by side in a single layout, every view side by side in model space.</summary>
    SingleLayout,
}

/// <summary>A viewport of a sheet's paper space and the model-space point it shows at its centre.</summary>
public sealed record ViewportCenter(string Handle, double X, double Y);

/// <summary>
/// What Revit wrote in a sheet's paper space: the layout that holds the title block, schedules and viewports, the
/// drawing units (INSUNITS), the layout's own overall viewport (not a view: it is left behind) and the view viewports.
/// </summary>
public sealed record PaperSpace(string Layout, int InsUnits, string? OverallViewport, IReadOnlyList<ViewportCenter> Viewports)
{
    /// <summary>Millimetres in one drawing unit.</summary>
    public double UnitMm => MergePlanner.MillimetersPerUnit(InsUnits);
}

/// <summary>
/// An exported sheet and the rectangle its model-space content occupies (drawing units). <paramref name="PaperWidthMm"/>
/// and <paramref name="PaperHeightMm"/> are the paper the sheet uses in Revit (its title block), when known.
/// <paramref name="OriginXMm"/> and <paramref name="OriginYMm"/> are the sheet's lower-left corner in Revit sheet
/// coordinates, which are the paper-space coordinates of the exported DWG. <paramref name="Space"/> is the paper space
/// read from the file; with it, the sheet's own layout is copied instead of rebuilt.
/// </summary>
public sealed record SheetExtent(string Name, string File, double X0, double Y0, double X1, double Y1,
    double? PaperWidthMm = null, double? PaperHeightMm = null,
    double? OriginXMm = null, double? OriginYMm = null, PaperSpace? Space = null)
{
    public bool HasPaper => PaperWidthMm is > 0 && PaperHeightMm is > 0;

    /// <summary>The sheet's real paper space can be copied: it was read, and the paper and its corner are known.</summary>
    public bool HasPaperSpace => Space is not null && HasPaper && OriginXMm is not null && OriginYMm is not null;

    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
}

/// <summary>
/// <paramref name="Paper"/> and <paramref name="Landscape"/> are only the fallback for a sheet whose paper Revit did not
/// report; a sheet with a known paper always gets exactly that paper.
/// </summary>
public sealed record MergeOptions(OutputMode Mode, string Paper = "A1", bool Landscape = true, string LayoutName = "Folhas");

/// <summary>Where a sheet was placed in the merged model space.</summary>
public sealed record Placement(SheetExtent Sheet, double OffsetX, double OffsetY, double X0, double Y0, double X1, double Y1);

/// <summary>
/// Builds the argument list for <c>cadcraft-cli run</c> that merges exported sheets into one drawing.
/// Pure: it only builds text, so it is tested without the tool.
/// </summary>
public static class MergePlanner
{
    /// <summary>Paper sizes in millimetres, landscape (long side first).</summary>
    public static readonly IReadOnlyDictionary<string, (double Width, double Height)> Papers =
        new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = (1189, 841),
            ["A1"] = (841, 594),
            ["A2"] = (594, 420),
            ["A3"] = (420, 297),
            ["A4"] = (297, 210),
        };

    /// <summary>How far (in millimetres) a measured sheet may be from an ISO A paper and still count as that paper.</summary>
    public const double PaperTolerance = 3;

    /// <summary>
    /// Revit reports a sheet's size from its outline, which is often a millimetre off (840 x 594 for an A1). Within
    /// <see cref="PaperTolerance"/> of an ISO A paper, in either orientation, the exact paper is returned; any other
    /// size is kept as measured.
    /// </summary>
    public static (double Width, double Height) SnapToStandardPaper(double widthMm, double heightMm)
    {
        foreach (var (width, height) in Papers.Values)
        {
            if (Math.Abs(widthMm - width) <= PaperTolerance && Math.Abs(heightMm - height) <= PaperTolerance) return (width, height);
            if (Math.Abs(widthMm - height) <= PaperTolerance && Math.Abs(heightMm - width) <= PaperTolerance) return (height, width);
        }
        return (Math.Round(widthMm, 1), Math.Round(heightMm, 1));
    }

    /// <summary>Space between sheets in model space, as a fraction of the widest sheet.</summary>
    public const double GapFraction = 0.25;

    /// <summary>Paper margin around the viewport, in millimetres.</summary>
    public const double PageMargin = 10;

    /// <summary>Extra room around the content inside a viewport, as a fraction of its size.</summary>
    public const double ViewMargin = 0.02;

    public static IReadOnlyList<Placement> Place(IReadOnlyList<SheetExtent> sheets)
    {
        var gap = sheets.Count == 0 ? 0 : sheets.Max(sheet => sheet.Width) * GapFraction;
        var cursor = 0.0;
        var placements = new List<Placement>();
        foreach (var sheet in sheets)
        {
            var dx = cursor - sheet.X0;
            var dy = -sheet.Y0;
            placements.Add(new Placement(sheet, dx, dy, cursor, 0, cursor + sheet.Width, sheet.Height));
            cursor += sheet.Width + gap;
        }
        return placements;
    }

    /// <summary>Space between sheets in a single layout, in millimetres of paper.</summary>
    public const double PaperGapMm = 50;

    /// <summary>
    /// Windows refuses a command line over 32 767 characters, so a long merge is split into several runs, each one
    /// reopening the drawing the previous one saved.
    /// </summary>
    public const int MaxRunLength = 24_000;

    /// <summary>Millimetres in one drawing unit for an INSUNITS code; unknown codes count as millimetres.</summary>
    public static double MillimetersPerUnit(int insUnits) => insUnits switch
    {
        1 => 25.4,
        2 => 304.8,
        5 => 10,
        6 => 1000,
        14 => 100,
        _ => 1,
    };

    /// <summary>The whole merge as one <c>cadcraft-cli run</c> argument list.</summary>
    public static List<string> Plan(IReadOnlyList<SheetExtent> sheets, MergeOptions options, string outputPath)
    {
        var steps = Steps(sheets, options);
        var args = Header(sheets);
        foreach (var step in steps) args.AddRange(step);
        args.Add("--save");
        args.Add(Slash(outputPath));
        return args;
    }

    /// <summary>
    /// The same merge split into runs short enough for the Windows command line. The first run starts a new drawing
    /// and every later one reopens and overwrites <paramref name="outputPath"/>. A sheet's steps never span two runs.
    /// </summary>
    public static List<List<string>> PlanRuns(IReadOnlyList<SheetExtent> sheets, MergeOptions options, string outputPath,
        int maxLength = MaxRunLength)
    {
        var output = Slash(outputPath);
        var steps = Steps(sheets, options);
        var runs = new List<List<string>>();
        var current = Header(sheets);
        var hasSteps = false;
        foreach (var step in steps)
        {
            if (hasSteps && Length(current) + Length(step) + output.Length + 32 > maxLength)
            {
                current.Add("--save");
                current.Add(output);
                runs.Add(current);
                current = ["run", output, "--in-place"];
                hasSteps = false;
            }
            current.AddRange(step);
            hasSteps = true;
        }
        current.Add("--save");
        current.Add(output);
        runs.Add(current);
        return runs;
    }

    private static int Length(IEnumerable<string> args) => args.Sum(arg => arg.Length + 3);

    private static List<string> Header(IReadOnlyList<SheetExtent> sheets)
    {
        var args = new List<string> { "run", "--metric" };
        // The merged drawing keeps the units Revit exported in, so distances and paper scales stay right.
        if (sheets.FirstOrDefault(sheet => sheet.Space is not null)?.Space is { InsUnits: > 0 } space)
            Command(args, "units", new { insunits = space.InsUnits });
        return args;
    }

    /// <summary>The commands of a merge, grouped so a group is never split between two runs.</summary>
    private static List<List<string>> Steps(IReadOnlyList<SheetExtent> sheets, MergeOptions options)
    {
        if (sheets.Count == 0) throw new ArgumentException("Nenhuma folha para mesclar.", nameof(sheets));
        if (options.Mode == OutputMode.SeparateFiles) throw new ArgumentException("Arquivos separados nao precisam de mesclagem.", nameof(options));

        var placements = Place(sheets);
        var steps = new List<List<string>>();

        // Model space: every view of every sheet, side by side.
        foreach (var placement in placements)
        {
            var step = new List<string>();
            Command(step, "import", new { path = Slash(placement.Sheet.File), at = new[] { placement.OffsetX, placement.OffsetY } });
            steps.Add(step);
        }

        // "Model" is reserved and a new drawing already has Layout1 and Layout2, so a sheet with one of those names gets a suffix.
        var usedLayouts = new HashSet<string> { "model", "layout1", "layout2" };
        var madeLayout = false;
        switch (options.Mode)
        {
            case OutputMode.MultipleLayouts:
                foreach (var placement in placements)
                {
                    var sheet = placement.Sheet;
                    var name = Unique(LayoutName(sheet.Name), usedLayouts);
                    var step = new List<string>();
                    if (sheet.HasPaperSpace)
                    {
                        // The sheet's own paper space (title block, schedules, notes, viewports), with its corner at 0,0.
                        CopyPaperSpace(step, placement);
                        NewLayout(step, name);
                        Command(step, "pasteclip", new { at = new[] { 0.0, 0.0 } });
                        PageSetup(step, name, sheet.PaperWidthMm!.Value, sheet.PaperHeightMm!.Value, sheet.Space!.UnitMm);
                    }
                    else if (sheet.HasPaper)
                    {
                        // The layout is the sheet's own paper, filled edge to edge by the sheet's content.
                        var (w, h) = (sheet.PaperWidthMm!.Value, sheet.PaperHeightMm!.Value);
                        AddLayout(step, name, null, w >= h, w, h, (placement.X0, placement.Y0, placement.X1, placement.Y1), margin: 0);
                    }
                    else
                    {
                        AddLayout(step, name, options.Paper, options.Landscape, null, null,
                            Window(placement.X0, placement.Y0, placement.X1, placement.Y1), PageMargin);
                    }
                    steps.Add(step);
                    madeLayout = true;
                }
                break;

            case OutputMode.SingleLayout when placements.All(placement => placement.Sheet.HasPaperSpace):
            {
                // Every sheet's own paper space, side by side on one page.
                var name = Unique(LayoutName(options.LayoutName), usedLayouts);
                var unitMm = sheets[0].Space!.UnitMm;
                var cursor = 0.0;
                var tallest = 0.0;
                var first = true;
                foreach (var placement in placements)
                {
                    var sheet = placement.Sheet;
                    var step = new List<string>();
                    CopyPaperSpace(step, placement);
                    if (first) NewLayout(step, name);
                    else Command(step, "layout.set", new { name });
                    Command(step, "pasteclip", new { at = new[] { cursor / sheet.Space!.UnitMm, 0.0 } });
                    steps.Add(step);
                    cursor += sheet.PaperWidthMm!.Value + PaperGapMm;
                    tallest = Math.Max(tallest, sheet.PaperHeightMm!.Value);
                    first = false;
                }
                var page = new List<string>();
                PageSetup(page, name, cursor - PaperGapMm, tallest, unitMm);
                steps.Add(page);
                madeLayout = true;
                break;
            }

            case OutputMode.SingleLayout:
            {
                var step = new List<string>();
                var x0 = placements.Min(p => p.X0);
                var y0 = placements.Min(p => p.Y0);
                var x1 = placements.Max(p => p.X1);
                var y1 = placements.Max(p => p.Y1);
                var (paperWidth, paperHeight) = Papers.TryGetValue(options.Paper, out var paper) ? paper : Papers["A1"];
                // The long side follows the largest paper among the sheets, so the layout is as big as the sheets themselves.
                var longSide = sheets.Where(sheet => sheet.HasPaper).Select(sheet => Math.Max(sheet.PaperWidthMm!.Value, sheet.PaperHeightMm!.Value))
                    .DefaultIfEmpty(Math.Max(paperWidth, paperHeight)).Max();
                var aspect = Math.Max((y1 - y0) / Math.Max(x1 - x0, 1e-9), 1e-6);
                // The paper keeps the long side of the chosen size and follows the content's proportion.
                var width = aspect <= 1 ? longSide : longSide / aspect;
                var height = aspect <= 1 ? longSide * aspect : longSide;
                AddLayout(step, Unique(LayoutName(options.LayoutName), usedLayouts), null, options.Landscape && width >= height,
                    Math.Max(width, 100), Math.Max(height, 100), Window(x0, y0, x1, y1), PageMargin);
                steps.Add(step);
                madeLayout = true;
                break;
            }
        }

        // The empty layouts every new drawing starts with go once the sheets have their own.
        if (madeLayout)
        {
            var cleanup = new List<string>();
            Command(cleanup, "layout.delete", new { name = "Layout1" });
            Command(cleanup, "layout.delete", new { name = "Layout2" });
            steps.Add(cleanup);
        }
        return steps;
    }

    /// <summary>
    /// Opens the sheet's own file, points its viewports at where its views now sit in the merged model space, copies its
    /// paper space with the sheet's lower-left corner as base point, and goes back to the merged drawing. The source file
    /// is closed without saving, so it is never changed on disk.
    /// </summary>
    private static void CopyPaperSpace(List<string> args, Placement placement)
    {
        var sheet = placement.Sheet;
        var space = sheet.Space!;
        Command(args, "open", new { path = Slash(sheet.File) });
        Command(args, "layout.set", new { name = space.Layout });
        // The layout's own overall viewport is not a view of the sheet; pasted, it would show the whole model.
        if (!string.IsNullOrEmpty(space.OverallViewport))
            Command(args, "erase", new { handles = new[] { space.OverallViewport } });
        foreach (var viewport in space.Viewports)
            Command(args, "viewport.set", new { handle = viewport.Handle, center = new[] { viewport.X + placement.OffsetX, viewport.Y + placement.OffsetY } });
        Command(args, "selectall", new { });
        Command(args, "copybase", new { @base = new[] { sheet.OriginXMm!.Value / space.UnitMm, sheet.OriginYMm!.Value / space.UnitMm } });
        Command(args, "close", new { });
        Command(args, "document.switch", new { index = 0 });
    }

    private static void NewLayout(List<string> args, string name)
    {
        Command(args, "layout.new", new { name, viewport = false });
        Command(args, "layout.set", new { name });
    }

    /// <summary>A page of the given paper, plotted at the drawing's scale so one paper millimetre prints as one millimetre.</summary>
    private static void PageSetup(List<string> args, string name, double widthMm, double heightMm, double unitMm) =>
        Command(args, "pagesetup", new
        {
            layout = name,
            width = Math.Round(widthMm, 1),
            height = Math.Round(heightMm, 1),
            landscape = widthMm >= heightMm,
            margins = new[] { 0, 0, 0, 0 },
            scale = unitMm,
        });

    private static (double X0, double Y0, double X1, double Y1) Window(double x0, double y0, double x1, double y1)
    {
        var padX = (x1 - x0) * ViewMargin;
        var padY = (y1 - y0) * ViewMargin;
        return (x0 - padX, y0 - padY, x1 + padX, y1 + padY);
    }

    private static void AddLayout(List<string> args, string name, string? paper, bool landscape,
        double? width, double? height, (double X0, double Y0, double X1, double Y1) window, double margin)
    {
        Command(args, "layout.new", new { name, viewport = false });
        Command(args, "layout.set", new { name });

        var (pageWidth, pageHeight) = width is not null && height is not null
            ? (width.Value, height.Value)
            : Papers.TryGetValue(paper ?? "A1", out var size) ? (landscape ? size.Width : size.Height, landscape ? size.Height : size.Width)
                                                             : (841.0, 594.0);
        if (width is not null && height is not null)
            Command(args, "pagesetup", new { layout = name, width = pageWidth, height = pageHeight, landscape });
        else
            Command(args, "pagesetup", new { layout = name, paper = paper ?? "A1", landscape });

        Command(args, "mview", new { p1 = new[] { margin, margin }, p2 = new[] { pageWidth - margin, pageHeight - margin } });
        Command(args, "mspace", new { });
        Command(args, "zoom", new { mode = "window", p1 = new[] { window.X0, window.Y0 }, p2 = new[] { window.X1, window.Y1 } });
        Command(args, "pspace", new { });
    }

    private static void Command(List<string> args, string id, object parameters)
    {
        args.Add("--cmd");
        args.Add($"{id} {JsonSerializer.Serialize(parameters, JsonOptions)}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };

    private static string Slash(string path) => path.Replace('\\', '/');

    private static readonly char[] InvalidLayoutChars = ['<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`'];

    /// <summary>Layout names forbid <c>&lt; &gt; / \ " : ; ? * | , = `</c> and stop at 255 characters.</summary>
    public static string LayoutName(string name)
    {
        var clean = new string(name.Select(ch => InvalidLayoutChars.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray()).Trim();
        if (clean.Length == 0) clean = "Layout";
        return clean.Length > 255 ? clean[..255] : clean;
    }

    private static string Unique(string name, ISet<string> used)
    {
        var candidate = name;
        for (var counter = 2; used.Contains(candidate.ToLowerInvariant()); counter++)
            candidate = $"{name}_{counter}";
        used.Add(candidate.ToLowerInvariant());
        return candidate;
    }
}
