using System.IO.Compression;
using System.Text.Json.Nodes;
using Gestaobim.Ai;
using Gestaobim.Render.Core;
using Xunit;

namespace GBRender.Core.Tests;

internal sealed class ScriptedImageAi(Func<IReadOnlyList<ImagePart>, string, ImageResult> responder) : IAiGateway
{
    public List<(string Model, string Prompt, IReadOnlyList<ImagePart> Images, string? Aspect, string? Size)> Calls { get; } = [];
    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancel = default) => Task.FromResult<IReadOnlyList<string>>(["img"]);
    public Task<string> ChatAsync(string model, string system, string user, CancellationToken cancel = default) => throw new NotSupportedException();

    public Task<ImageResult> GenerateImageAsync(string model, string prompt, IReadOnlyList<ImagePart> images, string? aspectRatio, string? imageSize, CancellationToken cancel = default)
    {
        Calls.Add((model, prompt, images, aspectRatio, imageSize));
        return Task.FromResult(responder(images, prompt));
    }
}

public class OffCenterCameraTests
{
    [Fact]
    public void AnOffCenterWindowShiftsWhereThingsLand()
    {
        var s = new Scene();
        var e = s.AddElement(Fixtures.El(1));
        s.AddQuad(e, new(-1, -1, 10), new(1, -1, 10), new(1, 1, 10), new(-1, 1, 10));
        // janela da direita descentrada: o quadrado (no eixo) cai na metade ESQUERDA da imagem
        var centered = Camera.CreatePerspectiveWindow(new(0, 0, 0), new(0, 0, 1), new(0, 1, 0), -0.5, 0.5, -0.5, 0.5);
        var shifted = Camera.CreatePerspectiveWindow(new(0, 0, 0), new(0, 0, 1), new(0, 1, 0), -0.1, 0.9, -0.5, 0.5);
        var a = Rasterizer.Render(s, centered, 200, 200).BoundsOf(0)!.Value;
        var b = Rasterizer.Render(s, shifted, 200, 200).BoundsOf(0)!.Value;
        Assert.True(Math.Abs((a.MinX + a.MaxX) / 2.0 - 100) < 3);          // centrado: no meio
        Assert.True(Math.Abs((b.MinX + b.MaxX) / 2.0 - 100) > 15);          // descentrado: fora do meio
        Assert.Equal(1.0, centered.Aspect, 6);
        Assert.Equal(1.0, shifted.Aspect, 6);
    }

    [Fact]
    public void RejectsInvertedWindows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Camera.CreatePerspectiveWindow(new(0, 0, 0), new(0, 0, 1), new(0, 1, 0), 0.5, -0.5, -0.5, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Camera.CreateOrthographicWindow(new(0, 0, 0), new(0, 0, 1), new(0, 1, 0), -1, 1, 1, -1));
    }

    [Fact]
    public void OrthographicWindowFramesTheRequestedRegion()
    {
        var s = new Scene();
        var e = s.AddElement(Fixtures.El(1));
        s.AddQuad(e, new(0, 0, 5), new(2, 0, 5), new(2, 2, 5), new(0, 2, 5));
        // camera com Right = -x (olhando +z, y para cima): a janela x de -3 a 1 enquadra o quadrado (x de 0 a 2 => cam.x de 0 a -2)
        var cam = Camera.CreateOrthographicWindow(new(0, 0, 0), new(0, 0, 1), new(0, 1, 0), -3, 1, -1, 3);
        var r = Rasterizer.Render(s, cam, 400, 400);
        var b = r.BoundsOf(0)!.Value;
        Assert.InRange(b.MinX, 99, 101);     // cam.x = -2 -> (−2 + 3)/4 * 400 = 100
        Assert.InRange(b.MaxX, 298, 300);    // cam.x =  0 -> 300
        Assert.InRange(b.MinY, 99, 101);     // y = 2 -> (3 - 2)/4 * 400 = 100
        Assert.InRange(b.MaxY, 298, 300);    // y = 0 -> 300
    }
}

public class RenderJobTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gbrender-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (Exception) { }
    }

    private static RenderPasses Passes(RenderQuality quality = RenderQuality.OneK)
    {
        var (scene, cam) = Fixtures.House();
        return RenderJob.BuildPasses(scene, cam, quality, new CameraMeta(1.55, 132), new ManifestOptions
        {
            UserPrompt = "render editorial",
            TargetEnvironment = "fim de tarde",
            Swatches = new Dictionary<string, byte[]> { ["poltrona-mole"] = Png.Encode(new RawImage(2, 2, PngKind.Rgb8, new byte[12])) },
        });
    }

    [Theory]
    [InlineData(RenderQuality.OneK, 16 / 9.0, 1024, 576)]
    [InlineData(RenderQuality.TwoK, 1.0, 1536, 1536)]
    [InlineData(RenderQuality.FourK, 3 / 4.0, 1536, 2048)]
    public void ResolutionFollowsQualityAndAspect(RenderQuality q, double aspect, int w, int h) => Assert.Equal((w, h), RenderJob.Resolution(q, aspect));

    [Fact]
    public void PassesHaveTheRequestedSizeAndAnAlignedManifest()
    {
        var passes = Passes();
        Assert.Equal((1024, 576), (passes.Width, passes.Height));
        Assert.Equal((1024, 576), (passes.Layers.Clay.Width, passes.Layers.Clay.Height));
        Assert.True(ManifestVerifier.Verify(passes.Manifest.Manifest, passes.Layers.Clay, passes.Layers.ColorId, passes.Layers.Depth, passes.Layers.Background).Ok);
    }

    [Fact]
    public void WritingPassesProducesAVerifiedBundleAndLooseFiles()
    {
        var zip = RenderJob.WritePasses(Passes(), _folder);
        Assert.True(File.Exists(zip));
        foreach (var name in new[] { "clay.png", "color_id.png", "depth.png", "background.png", "scene_manifest.json" })
            Assert.True(File.Exists(Path.Combine(_folder, name)), name);
        using var stream = File.OpenRead(zip);
        Assert.True(RenderBundle.Verify(stream).Ok);
        // as imagens soltas sao as mesmas do zip
        using var archive = new ZipArchive(File.OpenRead(zip));
        using var entry = archive.GetEntry("clay.png")!.Open();
        using var ms = new MemoryStream();
        entry.CopyTo(ms);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_folder, "clay.png")), ms.ToArray());
    }

    [Fact]
    public async Task GenerateSendsAllPassesAndSavesRenderPlusReportWithoutSecrets()
    {
        var passes = Passes();
        var output = Png.Encode(new RawImage(8, 8, PngKind.Rgb8, new byte[8 * 8 * 3]));
        var ai = new ScriptedImageAi((_, _) => new ImageResult(output, "image/png", "feito com token tok-segredo-0123456789"));
        var result = await RenderJob.GenerateAsync(ai, "img", passes, RenderQuality.TwoK, _folder, "tok-segredo-0123456789");
        Assert.Equal(output, File.ReadAllBytes(result.ImagePath));
        Assert.EndsWith(".png", result.ImagePath);
        var call = Assert.Single(ai.Calls);
        Assert.Equal("img", call.Model);
        Assert.Equal("16:9", call.Aspect);
        Assert.Equal("2K", call.Size);
        Assert.Equal(["0 clay", "1 color_id", "2 depth", "3 background", "4 swatch"], call.Images.Select(i => i.Label));

        var report = File.ReadAllText(result.ReportPath);
        Assert.DoesNotContain("tok-segredo", report);
        var json = JsonNode.Parse(report)!;
        Assert.Equal(RenderBundle.Sha256(output), json["OutputSha256"]!.ToString());
        Assert.Equal(RenderBundle.Sha256(Png.Encode(passes.Layers.ColorId)), json["InputSha256"]!["1 color_id"]!.ToString());
        Assert.True(json["InputSha256"]!["prompt"] is not null);
    }

    [Fact]
    public async Task GenerateWithoutAModelIsAClearError()
    {
        var ai = new ScriptedImageAi((_, _) => throw new InvalidOperationException("nao deve chamar"));
        await Assert.ThrowsAsync<AiException>(() => RenderJob.GenerateAsync(ai, "", Passes(), RenderQuality.OneK, _folder));
        Assert.Empty(ai.Calls);
    }

    [Fact]
    public async Task JpegAnswersKeepTheirExtension()
    {
        var ai = new ScriptedImageAi((_, _) => new ImageResult([0xFF, 0xD8, 0xFF], "image/jpeg", null));
        var result = await RenderJob.GenerateAsync(ai, "img", Passes(), RenderQuality.OneK, _folder);
        Assert.EndsWith(".jpg", result.ImagePath);
    }

    [Fact]
    public async Task EditChangesOnlyTheChosenObjectAndTheCheckProvesIt()
    {
        var passes = Passes();
        var previous = Png.Encode(new RawImage(passes.Width, passes.Height, PngKind.Rgb8, Enumerable.Repeat((byte)120, passes.Width * passes.Height * 3).ToArray()));
        var ai = new ScriptedImageAi((images, _) =>
        {
            var mask = Png.Decode(images[1].Bytes);
            var before = Png.Decode(images[0].Bytes);
            var data = (byte[])before.Data.Clone();
            for (var i = 0; i < mask.Data.Length; i++)
                if (mask.Data[i] > 127) { data[i * 3] = 20; data[i * 3 + 1] = 40; data[i * 3 + 2] = 220; }
            return new ImageResult(Png.Encode(new RawImage(before.Width, before.Height, PngKind.Rgb8, data)), "image/png", null);
        });
        var edited = await RenderJob.EditAsync(ai, "img", passes, previous, [482910], "tecido azul", "a poltrona", RenderQuality.OneK, _folder);
        Assert.NotNull(edited.Check);
        Assert.True(edited.Check!.Ok);
        Assert.Equal(0, edited.Check.ChangedOutside, 6);
        Assert.Equal("edit", edited.Render.Report.Kind);
        Assert.NotNull(edited.Render.Report.ChangedInsideMask);
        Assert.Contains("a poltrona", edited.Render.Report.EditedObject);
    }

    [Fact]
    public async Task EditNotesWhenTheModelChangedTheWholePicture()
    {
        var passes = Passes();
        var previous = Png.Encode(new RawImage(passes.Width, passes.Height, PngKind.Rgb8, Enumerable.Repeat((byte)120, passes.Width * passes.Height * 3).ToArray()));
        var ai = new ScriptedImageAi((_, _) => new ImageResult(Png.Encode(new RawImage(passes.Width, passes.Height, PngKind.Rgb8, Enumerable.Repeat((byte)230, passes.Width * passes.Height * 3).ToArray())), "image/png", null));
        var edited = await RenderJob.EditAsync(ai, "img", passes, previous, [482910], "tecido azul", "a poltrona", RenderQuality.OneK, _folder);
        Assert.False(edited.Check!.Ok);
        Assert.Contains("nao ficou restrita", edited.Render.Report.Note);
    }

    [Fact]
    public async Task EditOfAnInvisibleOrUnknownObjectFailsBeforeCallingTheModel()
    {
        var ai = new ScriptedImageAi((_, _) => throw new InvalidOperationException("nao deve chamar"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RenderJob.EditAsync(ai, "img", Passes(), [1, 2, 3], [999999], "x", "y", RenderQuality.OneK, _folder));
        Assert.Empty(ai.Calls);
    }

    [Fact]
    public async Task EditWithAnUndecodableAnswerStillSavesAndExplainsNothingWasChecked()
    {
        var passes = Passes();
        var ai = new ScriptedImageAi((_, _) => new ImageResult([0xFF, 0xD8, 0xFF, 0xE0], "image/jpeg", null));
        var edited = await RenderJob.EditAsync(ai, "img", passes, Png.Encode(passes.Layers.Clay), [482910], "x", "a poltrona", RenderQuality.OneK, _folder);
        Assert.Null(edited.Check);
        Assert.True(File.Exists(edited.Render.ImagePath));
    }
}
