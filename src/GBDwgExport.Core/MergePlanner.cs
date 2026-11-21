using System.Text.Json;

namespace Gestaobim.DwgExport.Core;

public enum OutputMode
{
    /// <summary>One file per sheet. No post-processing.</summary>
    SeparateFiles,

    /// <summary>One drawing, one layout per sheet.</summary>
    MultipleLayouts,

    /// <summary>One drawing, every sheet side by side in model space.</summary>
    ModelSpace,

    /// <summary>One drawing, one layout showing every sheet.</summary>
    SingleLayout,
}

/// <summary>An exported sheet and the rectangle its content occupies (drawing units).</summary>
public sealed record SheetExtent(string Name, string File, double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
}

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

    public static List<string> Plan(IReadOnlyList<SheetExtent> sheets, MergeOptions options, string outputPath)
    {
        if (sheets.Count == 0) throw new ArgumentException("Nenhuma folha para mesclar.", nameof(sheets));
        if (options.Mode == OutputMode.SeparateFiles) throw new ArgumentException("Arquivos separados nao precisam de mesclagem.", nameof(options));

        var placements = Place(sheets);
        var args = new List<string> { "run", "--metric" };
        foreach (var placement in placements)
            Command(args, "import", new { path = Slash(placement.Sheet.File), at = new[] { placement.OffsetX, placement.OffsetY } });

        // "Model" is reserved and a new drawing already has Layout1 and Layout2, so a sheet with one of those names gets a suffix.
        var usedLayouts = new HashSet<string> { "model", "layout1", "layout2" };
        switch (options.Mode)
        {
            case OutputMode.MultipleLayouts:
                foreach (var placement in placements)
                {
                    var name = Unique(LayoutName(placement.Sheet.Name), usedLayouts);
                    AddLayout(args, name, options.Paper, options.Landscape, null, null, Window(placement.X0, placement.Y0, placement.X1, placement.Y1));
                }
                break;

            case OutputMode.SingleLayout:
                var x0 = placements.Min(p => p.X0);
                var y0 = placements.Min(p => p.Y0);
                var x1 = placements.Max(p => p.X1);
                var y1 = placements.Max(p => p.Y1);
                var (paperWidth, paperHeight) = Papers.TryGetValue(options.Paper, out var paper) ? paper : Papers["A1"];
                var longSide = Math.Max(paperWidth, paperHeight);
                var aspect = Math.Max((y1 - y0) / Math.Max(x1 - x0, 1e-9), 1e-6);
                // The paper keeps the long side of the chosen size and follows the content's proportion.
                var width = aspect <= 1 ? longSide : longSide / aspect;
                var height = aspect <= 1 ? longSide * aspect : longSide;
                AddLayout(args, Unique(LayoutName(options.LayoutName), usedLayouts), null, options.Landscape && width >= height,
                    Math.Max(width, 100), Math.Max(height, 100), Window(x0, y0, x1, y1));
                break;
        }

        args.Add("--save");
        args.Add(Slash(outputPath));
        return args;
    }

    private static (double X0, double Y0, double X1, double Y1) Window(double x0, double y0, double x1, double y1)
    {
        var padX = (x1 - x0) * ViewMargin;
        var padY = (y1 - y0) * ViewMargin;
        return (x0 - padX, y0 - padY, x1 + padX, y1 + padY);
    }

    private static void AddLayout(List<string> args, string name, string? paper, bool landscape,
        double? width, double? height, (double X0, double Y0, double X1, double Y1) window)
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

        Command(args, "mview", new { p1 = new[] { PageMargin, PageMargin }, p2 = new[] { pageWidth - PageMargin, pageHeight - PageMargin } });
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
