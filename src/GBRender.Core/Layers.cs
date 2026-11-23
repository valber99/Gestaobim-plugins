using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Gestaobim.Render.Core;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public double DistanceTo(Rgb o)
    {
        double dr = R - o.R, dg = G - o.G, db = B - o.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    public static Rgb Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6) throw new FormatException($"Cor invalida: {hex}");
        return new Rgb(byte.Parse(h[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(h[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(h[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }
}

public enum ColorGrouping { Auto, PerElement, PerCategory }

/// <summary>A cor de cada elemento na mascara Color-ID. Todas diferentes entre si e do fundo.</summary>
public sealed class ColorAssignment
{
    public ColorAssignment(IReadOnlyList<Rgb> byElement, Rgb background, ColorGrouping grouping)
    {
        ByElement = byElement;
        Background = background;
        Grouping = grouping;
    }

    public IReadOnlyList<Rgb> ByElement { get; }
    public Rgb Background { get; }
    public ColorGrouping Grouping { get; }
}

public static class Palette
{
    /// <summary>Amarelo vivo, que nao aparece em material de obra: e o "fundo" da mascara e do manifesto.</summary>
    public static readonly Rgb DefaultBackground = new(0xFF, 0xE0, 0x33);

    private static readonly byte[] Levels = [16, 48, 80, 112, 144, 176, 208, 240];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Rgb, IReadOnlyList<Rgb>> Cache = new();

    /// <summary>
    /// Cores candidatas: reticulado 8x8x8, sem cinzas (parecem sombreamento) e longe do fundo, na ordem do ponto mais distante: cada cor
    /// e a mais diferente das ja escolhidas, entao os primeiros elementos recebem contraste maximo. Capacidade em torno de 490.
    /// </summary>
    public static IReadOnlyList<Rgb> Candidates(Rgb background) => Cache.GetOrAdd(background, Build);

    private static IReadOnlyList<Rgb> Build(Rgb background)
    {
        var pool = new List<Rgb>();
        foreach (var r in Levels)
            foreach (var g in Levels)
                foreach (var b in Levels)
                {
                    if (r == g && g == b) continue;
                    var c = new Rgb(r, g, b);
                    if (c.DistanceTo(background) < 60) continue;
                    pool.Add(c);
                }
        var ordered = new List<Rgb>(pool.Count);
        var minDistance = new double[pool.Count];
        Array.Fill(minDistance, double.MaxValue);
        var first = pool.Select((c, i) => (c, i)).OrderByDescending(t => Spread(t.c)).ThenBy(t => t.c.R).ThenBy(t => t.c.G).ThenBy(t => t.c.B).First().i;
        var taken = new bool[pool.Count];
        var next = first;
        while (true)
        {
            taken[next] = true;
            ordered.Add(pool[next]);
            if (ordered.Count == pool.Count) break;
            var best = -1;
            var bestValue = -1.0;
            for (var i = 0; i < pool.Count; i++)
            {
                if (taken[i]) continue;
                minDistance[i] = Math.Min(minDistance[i], pool[i].DistanceTo(pool[next]));
                if (minDistance[i] > bestValue + 1e-9) { bestValue = minDistance[i]; best = i; }
            }
            next = best;
        }
        return ordered;
    }

    private static int Spread(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    public static int Capacity(Rgb? background = null) => Candidates(background ?? DefaultBackground).Count;

    /// <summary>
    /// Uma cor por elemento visivel; se houver mais elementos que cores, uma por categoria. Elementos sem pixel na imagem nao
    /// gastam cor. A ordem e deterministica (por id), entao a mesma cena gera sempre as mesmas cores.
    /// </summary>
    public static ColorAssignment Assign(IReadOnlyList<SceneElement> elements, IReadOnlySet<int> visible, ColorGrouping grouping = ColorGrouping.Auto, Rgb? background = null)
    {
        var bg = background ?? DefaultBackground;
        var candidates = Candidates(bg);
        var colors = new Rgb[elements.Count];
        for (var i = 0; i < colors.Length; i++) colors[i] = bg;

        var visibleIndexes = visible.Where(i => i >= 0 && i < elements.Count).OrderBy(i => elements[i].Id).ToList();
        var mode = grouping == ColorGrouping.Auto
            ? (visibleIndexes.Count <= candidates.Count ? ColorGrouping.PerElement : ColorGrouping.PerCategory)
            : grouping;

        if (mode == ColorGrouping.PerElement)
        {
            if (visibleIndexes.Count > candidates.Count) throw new InvalidOperationException($"{visibleIndexes.Count} elementos visiveis excedem as {candidates.Count} cores distintas disponiveis.");
            for (var n = 0; n < visibleIndexes.Count; n++) colors[visibleIndexes[n]] = candidates[n];
        }
        else
        {
            var categories = visibleIndexes.Select(i => elements[i].Category).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
            if (categories.Count > candidates.Count) throw new InvalidOperationException($"{categories.Count} categorias excedem as cores disponiveis.");
            var byCategory = categories.Select((c, n) => (c, n)).ToDictionary(t => t.c, t => candidates[t.n], StringComparer.Ordinal);
            foreach (var i in visibleIndexes) colors[i] = byCategory[elements[i].Category];
        }
        return new ColorAssignment(colors, bg, mode);
    }
}

/// <summary>As quatro camadas, ja em imagens.</summary>
public sealed record RenderLayers(RawImage Clay, RawImage ColorId, RawImage Depth, RawImage Background, ColorAssignment Colors);

public static class Layers
{
    public const byte SkyValue = 255;
    public const byte EnvironmentValue = 128;
    public const byte ModelValue = 0;

    public static RenderLayers Build(RasterResult raster, ColorGrouping grouping = ColorGrouping.Auto, Rgb? background = null)
    {
        var visible = raster.Coverage().Keys.ToHashSet();
        var colors = Palette.Assign(raster.Elements, visible, grouping, background);
        return new RenderLayers(Clay(raster), ColorId(raster, colors), Depth(raster), BackgroundMask(raster), colors);
    }

    /// <summary>Base sombreada em tons de cinza, fundo claro. A geometria aqui e a que a IA nao pode alterar.</summary>
    public static RawImage Clay(RasterResult r)
    {
        var data = new byte[r.Width * r.Height * 3];
        for (var i = 0; i < r.Element.Length; i++)
        {
            var v = r.Element[i] >= 0 ? r.Shade[i] : (byte)245;
            data[i * 3] = data[i * 3 + 1] = data[i * 3 + 2] = v;
        }
        return new RawImage(r.Width, r.Height, PngKind.Rgb8, data);
    }

    /// <summary>Cor chapada por elemento, sem suavizacao de borda; o fundo tem a cor reservada.</summary>
    public static RawImage ColorId(RasterResult r, ColorAssignment colors)
    {
        var data = new byte[r.Width * r.Height * 3];
        for (var i = 0; i < r.Element.Length; i++)
        {
            var c = r.Element[i] >= 0 ? colors.ByElement[r.Element[i]] : colors.Background;
            data[i * 3] = c.R;
            data[i * 3 + 1] = c.G;
            data[i * 3 + 2] = c.B;
        }
        return new RawImage(r.Width, r.Height, PngKind.Rgb8, data);
    }

    /// <summary>Profundidade em 16 bits: perto = claro, longe = escuro, sem geometria = 0. Normalizada entre o mais perto e o mais longe visiveis.</summary>
    public static RawImage Depth(RasterResult r)
    {
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (var i = 0; i < r.Depth.Length; i++)
            if (r.Element[i] >= 0)
            {
                if (r.Depth[i] < min) min = r.Depth[i];
                if (r.Depth[i] > max) max = r.Depth[i];
            }
        var data = new byte[r.Width * r.Height * 2];
        var range = max - min;
        for (var i = 0; i < r.Depth.Length; i++)
        {
            ushort v = 0;
            if (r.Element[i] >= 0)
                v = range <= 1e-9f ? (ushort)65535 : (ushort)Math.Clamp(Math.Round(1 + (1 - (r.Depth[i] - min) / range) * 65534), 1, 65535);
            data[i * 2] = (byte)(v >> 8);
            data[i * 2 + 1] = (byte)v;
        }
        return new RawImage(r.Width, r.Height, PngKind.Gray16, data);
    }

    /// <summary>255 = ceu (nada na linha de visada), 128 = entorno (terreno, vegetacao), 0 = construcao e interior.</summary>
    public static RawImage BackgroundMask(RasterResult r)
    {
        var data = new byte[r.Width * r.Height];
        for (var i = 0; i < data.Length; i++)
        {
            var e = r.Element[i];
            data[i] = e < 0 ? SkyValue : r.Elements[e].Role == SceneRole.Environment ? EnvironmentValue : ModelValue;
        }
        return new RawImage(r.Width, r.Height, PngKind.Gray8, data);
    }

    /// <summary>Mascara binaria (255 onde esta um dos elementos) para a edicao localizada de um objeto.</summary>
    public static RawImage ObjectMask(RasterResult r, IEnumerable<int> elementIndexes)
    {
        var set = elementIndexes.ToHashSet();
        var data = new byte[r.Width * r.Height];
        for (var i = 0; i < data.Length; i++) data[i] = set.Contains(r.Element[i]) ? (byte)255 : (byte)0;
        return new RawImage(r.Width, r.Height, PngKind.Gray8, data);
    }
}
