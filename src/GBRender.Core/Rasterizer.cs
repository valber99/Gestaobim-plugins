using System;
using System.Collections.Generic;

namespace Gestaobim.Render.Core;

/// <summary>Papel do elemento na cena: o que fica atras da casa (entorno) tem tratamento proprio na mascara de fundo.</summary>
public enum SceneRole { Model, Environment }

/// <summary>Um elemento do modelo que aparece na cena: tudo que o manifesto precisa saber dele.</summary>
public sealed record SceneElement(
    long Id,
    string Category,
    string FamilyType,
    string? Manufacturer,
    string? Model,
    string? Material,
    SceneRole Role = SceneRole.Model,
    string? SwatchKey = null);

/// <summary>Triangulos do modelo, em metros, ja no sistema de coordenadas do mundo, com o indice do elemento dono.</summary>
public sealed class Scene
{
    private readonly List<SceneElement> _elements = [];
    private readonly Dictionary<long, int> _index = [];
    private readonly List<(Vec3 A, Vec3 B, Vec3 C, int Element)> _triangles = [];

    public IReadOnlyList<SceneElement> Elements => _elements;
    public int TriangleCount => _triangles.Count;
    internal IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C, int Element)> Triangles => _triangles;

    /// <summary>Registra o elemento (uma vez) e devolve o indice usado nos triangulos.</summary>
    public int AddElement(SceneElement element)
    {
        if (_index.TryGetValue(element.Id, out var existing)) return existing;
        _elements.Add(element);
        _index[element.Id] = _elements.Count - 1;
        return _elements.Count - 1;
    }

    public void AddTriangle(int elementIndex, Vec3 a, Vec3 b, Vec3 c)
    {
        if ((uint)elementIndex >= (uint)_elements.Count) throw new ArgumentOutOfRangeException(nameof(elementIndex));
        _triangles.Add((a, b, c, elementIndex));
    }

    /// <summary>Atalho para quadrilateros (duas triangulos).</summary>
    public void AddQuad(int elementIndex, Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        AddTriangle(elementIndex, a, b, c);
        AddTriangle(elementIndex, a, c, d);
    }
}

/// <summary>
/// O resultado do rasterizador: o mesmo z-buffer gera as quatro camadas, entao elas estao alinhadas pixel a pixel por construcao
/// (a imagem exportada pelo Revit teria outro enquadramento e suavizacao de bordas, que misturaria cores na mascara).
/// </summary>
public sealed class RasterResult
{
    public RasterResult(int width, int height, IReadOnlyList<SceneElement> elements)
    {
        Width = width;
        Height = height;
        Elements = elements;
        Depth = new float[width * height];
        Array.Fill(Depth, float.PositiveInfinity);
        Element = new int[width * height];
        Array.Fill(Element, -1);
        Shade = new byte[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<SceneElement> Elements { get; }
    public float[] Depth { get; }
    public int[] Element { get; }
    public byte[] Shade { get; }

    public bool IsCovered(int x, int y) => Element[y * Width + x] >= 0;

    /// <summary>Pixels cobertos por cada elemento (indice do elemento).</summary>
    public Dictionary<int, long> Coverage()
    {
        var result = new Dictionary<int, long>();
        foreach (var e in Element)
            if (e >= 0) result[e] = result.GetValueOrDefault(e) + 1;
        return result;
    }

    /// <summary>Menor caixa (em pixels, inclusive) que contem o elemento, ou null se ele nao aparece.</summary>
    public (int MinX, int MinY, int MaxX, int MaxY)? BoundsOf(int elementIndex)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                if (Element[y * Width + x] == elementIndex)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }
}

public static class Rasterizer
{
    public static RasterResult Render(Scene scene, Camera camera, int width, int height)
    {
        if (width is < 8 or > 16384 || height is < 8 or > 16384) throw new ArgumentOutOfRangeException(nameof(width), "Resolucao fora de 8..16384.");
        var result = new RasterResult(width, height, scene.Elements);
        var light = (camera.Forward * -0.8 + camera.Up * 0.5 + camera.Right * 0.3).Normalized();

        foreach (var (a, b, c, element) in scene.Triangles)
        {
            var normal = (b - a).Cross(c - a).Normalized();
            if (normal.Length < 0.5) continue;                                  // triangulo degenerado
            if (normal.Dot(camera.Forward) > 0) normal *= -1;                   // dupla face: a normal sempre olha para a camera
            var lum = 0.25 + 0.75 * Math.Max(0, normal.Dot(light));
            var shade = (byte)Math.Clamp((int)Math.Round(lum * 235), 0, 255);

            var poly = ClipNear([camera.ToCamera(a), camera.ToCamera(b), camera.ToCamera(c)], camera);
            if (poly.Count < 3) continue;
            for (var i = 1; i + 1 < poly.Count; i++)
                Fill(result, camera, poly[0], poly[i], poly[i + 1], element, shade);
        }
        return result;
    }

    private static List<Vec3> ClipNear(Vec3[] tri, Camera camera)
    {
        var near = camera.Perspective ? Math.Max(camera.Near, 1e-6) : camera.Near;
        var output = new List<Vec3>(4);
        for (var i = 0; i < 3; i++)
        {
            var cur = tri[i];
            var nxt = tri[(i + 1) % 3];
            var curIn = cur.Z >= near;
            var nxtIn = nxt.Z >= near;
            if (curIn) output.Add(cur);
            if (curIn != nxtIn)
            {
                var t = (near - cur.Z) / (nxt.Z - cur.Z);
                output.Add(new Vec3(cur.X + (nxt.X - cur.X) * t, cur.Y + (nxt.Y - cur.Y) * t, near));
            }
        }
        return output;
    }

    private static void Fill(RasterResult result, Camera camera, Vec3 p0, Vec3 p1, Vec3 p2, int element, byte shade)
    {
        int w = result.Width, h = result.Height;
        if (!camera.ToPixel(p0, w, h, out var x0, out var y0) || !camera.ToPixel(p1, w, h, out var x1, out var y1) || !camera.ToPixel(p2, w, h, out var x2, out var y2)) return;

        var area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (Math.Abs(area) < 1e-12) return;

        var minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
        var maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
        var maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
        if (minX > maxX || minY > maxY) return;

        // Em perspectiva 1/z varia linearmente na tela; em ortografica o proprio z.
        double v0, v1, v2;
        if (camera.Perspective) { v0 = 1 / p0.Z; v1 = 1 / p1.Z; v2 = 1 / p2.Z; }
        else { v0 = p0.Z; v1 = p1.Z; v2 = p2.Z; }

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5;
                var w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area;
                var w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area;
                var w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                var interpolated = w0 * v0 + w1 * v1 + w2 * v2;
                var z = camera.Perspective ? 1 / interpolated : interpolated;
                var index = y * w + x;
                if (z >= result.Depth[index]) continue;
                result.Depth[index] = (float)z;
                result.Element[index] = element;
                result.Shade[index] = shade;
            }
        }
    }
}
