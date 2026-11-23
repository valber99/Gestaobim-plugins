using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gestaobim.Ai;

namespace Gestaobim.Render.Core;

/// <summary>As quatro camadas, o manifesto e a resolucao de um render, prontos para gravar ou enviar.</summary>
public sealed record RenderPasses(RasterResult Raster, RenderLayers Layers, ManifestResult Manifest, Camera Camera, int Width, int Height);

public sealed record GeneratedRender(string ImagePath, string ReportPath, byte[] Bytes, string Mime, RenderReport Report);

public sealed record EditedRender(GeneratedRender Render, EditCheckResult? Check);

/// <summary>Os passos de um render, sem Revit: montar as camadas, gravar o pacote, chamar o modelo de imagem, editar um objeto.</summary>
public static class RenderJob
{
    public static (int Width, int Height) Resolution(RenderQuality quality, double aspect)
    {
        var longSide = quality.PassLongSide();
        return aspect >= 1
            ? (longSide, Math.Max(8, (int)Math.Round(longSide / aspect)))
            : (Math.Max(8, (int)Math.Round(longSide * aspect)), longSide);
    }

    public static RenderPasses BuildPasses(Scene scene, Camera camera, RenderQuality quality, CameraMeta meta, ManifestOptions options)
    {
        var (w, h) = Resolution(quality, camera.Aspect);
        var raster = Rasterizer.Render(scene, camera, w, h);
        if (raster.Coverage().Count == 0) throw new InvalidOperationException("Nenhum elemento aparece no enquadramento desta vista.");
        var layers = Layers.Build(raster);
        var manifest = ManifestBuilder.Build(raster, layers, camera, meta, options);
        return new RenderPasses(raster, layers, manifest, camera, w, h);
    }

    /// <summary>Grava as quatro imagens, o manifesto e o render_bundle.zip na pasta. Devolve o caminho do zip.</summary>
    public static string WritePasses(RenderPasses passes, string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, RenderBundle.ClayName), Png.Encode(passes.Layers.Clay));
        File.WriteAllBytes(Path.Combine(folder, RenderBundle.ColorIdName), Png.Encode(passes.Layers.ColorId));
        File.WriteAllBytes(Path.Combine(folder, RenderBundle.DepthName), Png.Encode(passes.Layers.Depth));
        File.WriteAllBytes(Path.Combine(folder, RenderBundle.BackgroundName), Png.Encode(passes.Layers.Background));
        File.WriteAllText(Path.Combine(folder, RenderBundle.ManifestName), passes.Manifest.Manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        var zipPath = Path.Combine(folder, "render_bundle.zip");
        using (var stream = File.Create(zipPath)) RenderBundle.Write(stream, passes.Layers, passes.Manifest);
        using var check = File.OpenRead(zipPath);
        var verification = RenderBundle.Verify(check);
        if (!verification.Ok) throw new InvalidDataException("O pacote de camadas não passou na conferência: " + string.Join("; ", verification.Problems.Take(3)));
        return zipPath;
    }

    public static async Task<GeneratedRender> GenerateAsync(IAiGateway ai, string model, RenderPasses passes, RenderQuality quality, string folder, string? token = null, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new AiException("Nenhum modelo de imagem foi escolhido. Abra \"Configurar IA\".");
        Directory.CreateDirectory(folder);
        var request = RenderPrompt.Build(passes.Layers, passes.Manifest, quality);
        var report = new RenderReport { Kind = "render", Model = model, Width = passes.Width, Height = passes.Height, ObjectCount = passes.Manifest.ObjectCount };
        AddInputHashes(report, request);
        var timer = Stopwatch.StartNew();
        var aspect = passes.Manifest.Manifest["camera"]?["aspect_ratio"]?.ToString();
        var image = await ai.GenerateImageAsync(model, request.Prompt, request.Images, aspect, quality.ToImageSize(), cancel).ConfigureAwait(false);
        report.DurationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 2);
        return Save(image, report, folder, "render", token);
    }

    /// <summary>
    /// Troca so o(s) objeto(s) da mascara. <paramref name="decoder"/> converte o render (PNG ou JPEG) em pixels para a conferencia de que so a
    /// regiao da mascara mudou; sem ele so PNG e conferido.
    /// </summary>
    public static async Task<EditedRender> EditAsync(IAiGateway ai, string model, RenderPasses passes, byte[] previousRender, IEnumerable<long> elementIds, string instruction,
        string description, RenderQuality quality, string folder, Func<byte[], RawImage?>? decoder = null, string? token = null, CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new AiException("Nenhum modelo de imagem foi escolhido. Abra \"Configurar IA\".");
        var ids = elementIds.ToHashSet();
        var indexes = Enumerable.Range(0, passes.Raster.Elements.Count).Where(i => ids.Contains(passes.Raster.Elements[i].Id)).ToList();
        if (indexes.Count == 0) throw new InvalidOperationException("O objeto escolhido nao aparece na vista.");
        var mask = Layers.ObjectMask(passes.Raster, indexes);
        if (!mask.Data.Any(v => v != 0)) throw new InvalidOperationException("O objeto escolhido nao tem pixels visiveis nesta vista.");

        Directory.CreateDirectory(folder);
        var request = RenderPrompt.BuildEdit(previousRender, mask, instruction, description);
        var report = new RenderReport { Kind = "edit", Model = model, Width = passes.Width, Height = passes.Height, EditedObject = description, ObjectCount = indexes.Count };
        AddInputHashes(report, request);
        var timer = Stopwatch.StartNew();
        var aspect = passes.Manifest.Manifest["camera"]?["aspect_ratio"]?.ToString();
        var image = await ai.GenerateImageAsync(model, request.Prompt, request.Images, aspect, quality.ToImageSize(), cancel).ConfigureAwait(false);
        report.DurationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 2);

        EditCheckResult? check = null;
        try
        {
            decoder ??= bytes => { try { return Png.Decode(bytes); } catch (Exception) { return null; } };
            var before = decoder(previousRender);
            var after = decoder(image.Bytes);
            if (before is { Kind: PngKind.Rgb8 } && after is { Kind: PngKind.Rgb8 })
            {
                before = EditCheck.ResizeNearest(before, passes.Width, passes.Height);
                after = EditCheck.ResizeNearest(after, passes.Width, passes.Height);
                check = EditCheck.Evaluate(before, after, mask);
                report.ChangedInsideMask = Math.Round(check.ChangedInside, 4);
                report.ChangedOutsideMask = Math.Round(check.ChangedOutside, 4);
                if (!check.Ok) report.Note = "Conferencia: a edicao nao ficou restrita a mascara (ou nao mudou o objeto). Confira a imagem.";
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException)
        {
            report.Note = "Nao foi possivel conferir a edicao: " + error.Message;
        }
        return new EditedRender(Save(image, report, folder, "edicao", token), check);
    }

    private static void AddInputHashes(RenderReport report, RenderRequest request)
    {
        foreach (var image in request.Images) report.InputSha256[image.Label] = RenderBundle.Sha256(image.Bytes);
        report.InputSha256["prompt"] = RenderBundle.Sha256(Encoding.UTF8.GetBytes(request.Prompt));
    }

    private static GeneratedRender Save(ImageResult image, RenderReport report, string folder, string prefix, string? token)
    {
        var extension = image.MimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : image.MimeType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? ".webp" : ".png";
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var imagePath = UniquePath(folder, $"{prefix}_{stamp}", extension);
        File.WriteAllBytes(imagePath, image.Bytes);
        report.OutputSha256 = RenderBundle.Sha256(image.Bytes);
        report.OutputBytes = image.Bytes.Length;
        report.OutputMime = image.MimeType;
        if (!string.IsNullOrWhiteSpace(image.Text)) report.Note = (report.Note is null ? "" : report.Note + " ") + "Resposta do modelo: " + Redactor.Shorten(Redactor.Scrub(image.Text, token), 300);
        var reportPath = Path.ChangeExtension(imagePath, ".report.json");
        File.WriteAllText(reportPath, report.ToJson(token), new UTF8Encoding(false));
        return new GeneratedRender(imagePath, reportPath, image.Bytes, image.MimeType, report);
    }

    private static string UniquePath(string folder, string name, string extension)
    {
        var path = Path.Combine(folder, name + extension);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name}-{i}{extension}");
        return path;
    }
}
