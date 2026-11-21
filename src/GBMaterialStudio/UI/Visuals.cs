using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Point = System.Windows.Point;

namespace Gestaobim.MaterialStudio.UI;

/// <summary>Brushes and previews shown in the editor.</summary>
public static class Visuals
{
    private static readonly Dictionary<long, Brush> PatternCache = [];

    public static SolidColorBrush ToBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    public static string Hex(int r, int g, int b) => $"#{r:X2}{g:X2}{b:X2}";

    public static (int R, int G, int B)? ParseHex(string? hex)
    {
        hex = (hex ?? "").TrimStart('#');
        try
        {
            return hex.Length switch
            {
                8 => (Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16), Convert.ToInt32(hex[6..8], 16)),
                6 => (Convert.ToInt32(hex[0..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..6], 16)),
                _ => null,
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Two flat halves, used to preview roughness, glossiness and the like.</summary>
    public static Brush Gradient(string left, string right, double width = 56, double height = 20)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(ToBrush(left), null, new RectangleGeometry(new Rect(0, 0, width, height))));
        group.Children.Add(new GeometryDrawing(ToBrush(right), null, new RectangleGeometry(new Rect(width / 2, 0, width / 2, height))));
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    public static Brush Checker(string colorA, string colorB, int width = 56, int height = 20, int size = 8)
    {
        var group = new DrawingGroup();
        for (var y = 0; y < height; y += size)
        {
            for (var x = 0; x < width; x += size)
            {
                var color = ((x / size) + (y / size)) % 2 < 1 ? colorA : colorB;
                group.Children.Add(new GeometryDrawing(ToBrush(color), null, new RectangleGeometry(new Rect(x, y, size, size))));
            }
        }
        return new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    /// <summary>The texture file as an image brush, or null when it is missing or not a format WPF can decode.</summary>
    public static Brush? TextureBrush(string? imagePath)
    {
        try
        {
            var resolved = TexturePaths.Resolve(imagePath);
            if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved)) return null;
            BitmapFrame frame;
            using (var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            }
            frame.Freeze();
            var brush = new ImageBrush(frame) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A small drawing that suggests a fill pattern (lines, dots, dashes, solid).</summary>
    public static Brush? PatternPreview(FillPatternElement? element)
    {
        if (element is null) return null;
        var key = element.Id.Value;
        if (PatternCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var pattern = element.GetFillPattern();
            if (pattern is null) return null;
            if (pattern.IsSolidFill)
                return Cache(key, ToBrush("#BFC7D1"));

            const double width = 56, height = 28;
            var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(new Rect(0, 0, width, height)) };
            group.Children.Add(new GeometryDrawing(ToBrush("#FAFAFA"), null, new RectangleGeometry(new Rect(0, 0, width, height))));

            IList<FillGrid> grids;
            try { grids = pattern.GetFillGrids(); }
            catch (Exception) { grids = []; }

            if (grids.Count > 0)
            {
                for (var i = 0; i < Math.Min(grids.Count, 4); i++)
                    AddGrid(group, grids[i], i, width, height);
            }
            else
            {
                var pen = new Pen(ToBrush("#46515C"), 1.0);
                for (var y = 6; y < 28; y += 6)
                    group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(2, y), new Point(54, y))));
            }

            var brush = new DrawingBrush(group)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height),
                Stretch = Stretch.Fill,
            };
            group.Freeze();
            brush.Freeze();
            return Cache(key, brush);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Brush Cache(long key, Brush brush)
    {
        PatternCache[key] = brush;
        return brush;
    }

    private static double Mod(double a, double b) => a - b * Math.Floor(a / b);

    private static void AddGrid(DrawingGroup group, FillGrid grid, int index, double width, double height)
    {
        var angle = grid.Angle;
        if (Math.Abs(angle) > 2 * Math.PI)
            angle = angle * Math.PI / 180.0;

        var spacing = Math.Abs(grid.Offset);
        var spacingPx = Math.Max(4.0, Math.Min(18.0, spacing * 24.0));
        var originShift = spacingPx > 0 ? Mod((grid.Origin.U + grid.Origin.V) * 12.0, spacingPx) : 0.0;

        IList<double> segments;
        try { segments = grid.GetSegments(); }
        catch (Exception) { segments = []; }

        var color = index == 0 ? "#46515C" : "#66717C";
        var pen = new Pen(ToBrush(color), index == 0 ? 1.1 : 0.9);
        var dashes = DashValues(segments);
        if (dashes.Length > 0)
            pen.DashStyle = new DashStyle(dashes, 0);

        double dirX = Math.Cos(angle), dirY = Math.Sin(angle);
        double normX = -dirY, normY = dirX;
        double centerX = width / 2.0, centerY = height / 2.0;
        const double lineHalf = 120.0;
        var isDotPattern = segments.Count > 0 && segments.All(s => Math.Abs(s) < 1e-6);

        for (var i = -12; i <= 12; i++)
        {
            var offset = originShift + i * spacingPx;
            var baseX = centerX + normX * offset;
            var baseY = centerY + normY * offset;
            if (isDotPattern)
            {
                var dot = index == 0 ? 1.6 : 1.2;
                group.Children.Add(new GeometryDrawing(ToBrush(color), null,
                    new EllipseGeometry(new Point(baseX, baseY), dot, dot)));
                continue;
            }
            group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(
                new Point(baseX - dirX * lineHalf, baseY - dirY * lineHalf),
                new Point(baseX + dirX * lineHalf, baseY + dirY * lineHalf))));
        }
    }

    private static double[] DashValues(IList<double> segments)
    {
        var values = new List<double>();
        foreach (var segment in segments)
        {
            if (Math.Abs(segment) < 1e-6) values.AddRange([1.0, 1.8]);
            else values.Add(Math.Max(1.0, Math.Min(14.0, Math.Abs(segment) * 8.0)));
        }
        return values.ToArray();
    }
}

/// <summary>Finds the file behind the path Revit stores in an appearance asset.</summary>
public static class TexturePaths
{
    private static IEnumerable<string> SearchRoots()
    {
        var roots = new List<string>();
        foreach (var variable in new[] { "CommonProgramFiles", "CommonProgramFiles(x86)" })
        {
            var baseFolder = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(baseFolder)) continue;
            roots.Add(Path.Combine(baseFolder, "Autodesk Shared", "Materials", "Textures"));
            roots.Add(Path.Combine(baseFolder, "Autodesk Shared", "Materials"));
        }
        return roots.Where(Directory.Exists).Distinct();
    }

    private static IEnumerable<string> Candidates(string pathText)
    {
        foreach (var chunk in pathText.Trim().Trim('"').Split('|'))
        {
            var candidate = chunk.Trim();
            if (candidate.Length == 0) continue;
            candidate = Environment.ExpandEnvironmentVariables(candidate);
            if (candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { candidate = new Uri(candidate).LocalPath; }
                catch (UriFormatException) { }
            }
            yield return candidate.Replace('/', '\\');
        }
    }

    /// <summary>The existing file for the path, or the last candidate tried when none exists. Empty for no path.</summary>
    public static string Resolve(string? pathText)
    {
        if (string.IsNullOrWhiteSpace(pathText)) return "";
        var roots = SearchRoots().ToList();
        var last = "";
        foreach (var candidate in Candidates(pathText))
        {
            last = candidate;
            if (File.Exists(candidate)) return candidate;
            foreach (var root in roots)
            {
                var joined = Path.Combine(root, candidate);
                if (File.Exists(joined)) return joined;
            }
        }
        return last;
    }

    public static string FileName(string? pathText)
    {
        var resolved = Resolve(pathText);
        return string.IsNullOrEmpty(resolved) ? pathText ?? "" : Path.GetFileName(resolved);
    }
}
