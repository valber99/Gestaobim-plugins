using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gestaobim.Render.Core;

/// <summary>Dados da camera que nao vem do rasterizador: altura do olho e sol da vista.</summary>
public sealed record CameraMeta(double EyeHeightMeters, double? SunAzimuthDegrees);

public sealed class ManifestOptions
{
    /// <summary>Texto livre do arquiteto sobre o fundo ("fim de tarde tropical...").</summary>
    public string TargetEnvironment { get; init; } = "";

    /// <summary>replace_exterior_only (troca so o ceu e o entorno visto pelas janelas) ou keep_background.</summary>
    public string BackgroundMode { get; init; } = "replace_exterior_only";

    public string UserPrompt { get; init; } = "";

    /// <summary>Tira fabricante e modelo (a configuracao de privacidade da janela).</summary>
    public bool StripManufacturers { get; init; }

    /// <summary>Miniaturas do catalogo por chave (SceneElement.SwatchKey). No maximo <see cref="MaxSwatches"/> seguem para a IA.</summary>
    public IReadOnlyDictionary<string, byte[]> Swatches { get; init; } = new Dictionary<string, byte[]>();

    public int MaxSwatches { get; init; } = 3;
}

public sealed record SwatchImage(string Key, byte[] Png, int ImageIndex);

public sealed record ManifestResult(JsonObject Manifest, IReadOnlyList<SwatchImage> Swatches, int ObjectCount);

public static class ManifestBuilder
{
    public const int FixedImages = 4;   // clay, color_id, depth, background; as miniaturas vem depois

    public static ManifestResult Build(RasterResult raster, RenderLayers layers, Camera camera, CameraMeta meta, ManifestOptions options)
    {
        var coverage = raster.Coverage();
        var total = (double)raster.Width * raster.Height;

        // Miniaturas: as dos elementos que mais aparecem entre os que tem imagem no catalogo.
        var swatches = new List<SwatchImage>();
        var swatchIndex = new Dictionary<int, int>();
        foreach (var (element, pixels) in coverage.OrderByDescending(c => c.Value).ThenBy(c => raster.Elements[c.Key].Id))
        {
            var key = raster.Elements[element].SwatchKey;
            if (key is null || !options.Swatches.TryGetValue(key, out var png)) continue;
            var existing = swatches.FirstOrDefault(s => s.Key == key);
            if (existing is null)
            {
                if (swatches.Count >= options.MaxSwatches) continue;
                existing = new SwatchImage(key, png, FixedImages + swatches.Count);
                swatches.Add(existing);
            }
            swatchIndex[element] = existing.ImageIndex;
        }

        // Um objeto por cor: um elemento (o normal) ou uma categoria inteira (quando faltaram cores).
        var byColor = coverage.Keys
            .GroupBy(e => layers.Colors.ByElement[e])
            .OrderBy(g => g.Min(e => raster.Elements[e].Id))
            .ToList();

        var objects = new JsonArray();
        foreach (var group in byColor)
        {
            var members = group.OrderBy(e => raster.Elements[e].Id).ToList();
            var first = raster.Elements[members[0]];
            var perCategory = members.Count > 1 || layers.Colors.Grouping == ColorGrouping.PerCategory;
            var obj = new JsonObject
            {
                ["id"] = perCategory ? $"cat_{first.Category}" : $"elem_{first.Id}",
                ["mask_color"] = group.Key.Hex,
                ["category"] = first.Category,
            };
            if (perCategory) obj["element_ids"] = new JsonArray(members.Select(e => (JsonNode?)raster.Elements[e].Id).ToArray());
            if (!perCategory)
            {
                obj["family_type"] = first.FamilyType;
                if (!options.StripManufacturers)
                {
                    if (!string.IsNullOrWhiteSpace(first.Manufacturer)) obj["manufacturer"] = first.Manufacturer;
                    if (!string.IsNullOrWhiteSpace(first.Model)) obj["model"] = first.Model;
                }
                if (!string.IsNullOrWhiteSpace(first.Material)) obj["material"] = first.Material;
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            long pixels = 0;
            foreach (var e in members)
            {
                pixels += coverage[e];
                if (raster.BoundsOf(e) is { } b)
                {
                    minX = Math.Min(minX, b.MinX); minY = Math.Min(minY, b.MinY);
                    maxX = Math.Max(maxX, b.MaxX); maxY = Math.Max(maxY, b.MaxY);
                }
            }
            obj["screen_bbox_uv"] = new JsonArray(Round(minX / (double)raster.Width), Round(minY / (double)raster.Height),
                Round((maxX + 1) / (double)raster.Width), Round((maxY + 1) / (double)raster.Height));
            obj["coverage"] = Round(pixels / total);
            if (!perCategory && swatchIndex.TryGetValue(members[0], out var imageIndex)) obj["reference_image_index"] = imageIndex;
            objects.Add(obj);
        }

        var manifest = new JsonObject
        {
            ["schema"] = 1,
            ["image"] = new JsonObject { ["width"] = raster.Width, ["height"] = raster.Height },
            ["camera"] = new JsonObject
            {
                ["perspective"] = camera.Perspective,
                ["lens_mm"] = camera.LensMm is { } lens ? Math.Round(lens, 1) : null,
                ["eye_height_m"] = Math.Round(meta.EyeHeightMeters, 2),
                ["aspect_ratio"] = AspectRatio(raster.Width, raster.Height),
                ["sun_azimuth_deg"] = meta.SunAzimuthDegrees is { } sun ? Math.Round(sun) : null,
            },
            ["background_layer"] = new JsonObject
            {
                ["mask_color"] = layers.Colors.Background.Hex,
                ["mode"] = options.BackgroundMode,
                ["target_environment"] = options.TargetEnvironment,
            },
            ["layers"] = new JsonArray("clay", "color_id", "depth", "background"),
            ["scene_objects"] = objects,
            ["user_prompt"] = options.UserPrompt,
        };
        return new ManifestResult(manifest, swatches, objects.Count);
    }

    private static double Round(double v) => Math.Round(v, 4);

    public static string AspectRatio(int width, int height)
    {
        var ratio = (double)width / height;
        foreach (var (name, value) in new[] { ("1:1", 1.0), ("4:3", 4 / 3.0), ("3:2", 1.5), ("16:9", 16 / 9.0), ("21:9", 21 / 9.0), ("3:4", 0.75), ("2:3", 2 / 3.0), ("9:16", 9 / 16.0) })
            if (Math.Abs(ratio - value) < 0.02) return name;
        var g = Gcd(width, height);
        return $"{width / g}:{height / g}";
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}

/// <summary>A conferencia da fase 2: as quatro imagens batem e toda cor da mascara esta no manifesto.</summary>
public sealed record VerificationResult(IReadOnlyList<string> Problems, int ColorsInImage, int ColorsInManifest)
{
    public bool Ok => Problems.Count == 0;
}

public static class ManifestVerifier
{
    public static VerificationResult Verify(JsonNode manifest, RawImage clay, RawImage colorId, RawImage depth, RawImage background)
    {
        var problems = new List<string>();
        var images = new[] { ("clay", clay), ("color_id", colorId), ("depth", depth), ("background", background) };
        foreach (var (name, image) in images.Skip(1))
            if (image.Width != clay.Width || image.Height != clay.Height)
                problems.Add($"{name} tem {image.Width}x{image.Height} e clay tem {clay.Width}x{clay.Height}.");
        if (colorId.Kind != PngKind.Rgb8) problems.Add("color_id precisa ser RGB de 8 bits.");
        if (depth.Kind != PngKind.Gray16) problems.Add("depth precisa ser cinza de 16 bits.");
        if (background.Kind != PngKind.Gray8) problems.Add("background precisa ser cinza de 8 bits.");
        if (problems.Count > 0) return new VerificationResult(problems, 0, 0);

        var bgText = manifest["background_layer"]?["mask_color"]?.ToString();
        if (bgText is null) return new VerificationResult(["O manifesto nao tem background_layer.mask_color."], 0, 0);
        var bg = Rgb.Parse(bgText);

        var manifestColors = new Dictionary<Rgb, string>();
        foreach (var obj in manifest["scene_objects"] as JsonArray ?? [])
        {
            var color = Rgb.Parse(obj!["mask_color"]!.ToString());
            if (color.Equals(bg)) problems.Add($"{obj["id"]} usa a cor do fundo.");
            if (!manifestColors.TryAdd(color, obj["id"]!.ToString())) problems.Add($"{obj["id"]} e {manifestColors[color]} usam a mesma cor {color.Hex}.");
        }

        var seen = new HashSet<Rgb>();
        var w = colorId.Width;
        for (var y = 0; y < colorId.Height; y++)
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = colorId.Rgb(x, y);
                var color = new Rgb(r, g, b);
                var hasGeometry = depth.Gray16(x, y) != 0;
                var isBgColor = color.Equals(bg);
                if (hasGeometry == isBgColor)
                {
                    problems.Add($"Pixel ({x},{y}): profundidade {(hasGeometry ? "com" : "sem")} geometria mas cor {(isBgColor ? "de fundo" : color.Hex)}.");
                    goto done;
                }
                var sky = background.Rgb(x, y).R == Layers.SkyValue;
                if (sky != !hasGeometry)
                {
                    problems.Add($"Pixel ({x},{y}): a mascara de fundo discorda da profundidade.");
                    goto done;
                }
                if (hasGeometry) seen.Add(color);
            }
        done:
        foreach (var color in seen.Where(c => !manifestColors.ContainsKey(c)))
            problems.Add($"A cor {color.Hex} aparece na mascara e nao esta no manifesto.");
        foreach (var (color, id) in manifestColors.Where(m => !seen.Contains(m.Key)))
            problems.Add($"{id} ({color.Hex}) esta no manifesto mas nao aparece na mascara.");
        return new VerificationResult(problems, seen.Count, manifestColors.Count);
    }
}
