using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.Ai.Revit;
using Gestaobim.Render.Core;

namespace Gestaobim.Render.Revit;

/// <summary>
/// Autoverificacao DENTRO de um Revit de verdade, sem interface e sem rede: so age quando GB_RENDER_SELFTEST aponta para a pasta do
/// relatorio e GB_RENDER_SELFTEST_MODELS lista projetos (separados por ';'). Cada projeto e aberto desanexado, nunca e salvo. Em uma vista
/// 3D por projeto: extrai a geometria (CustomExporter), monta as quatro camadas e o manifesto, e confere o criterio da fase 2:
/// quatro PNG do mesmo tamanho e 100% das cores da mascara no manifesto. Confere tambem a camera: a caixa projetada de cada elemento
/// grande tem de cruzar a regiao que o proprio elemento ocupa na imagem. Grava as camadas para conferir a olho.
/// </summary>
internal static class SelfTest
{
    public const string EnvFolder = "GB_RENDER_SELFTEST";
    public const string EnvModels = "GB_RENDER_SELFTEST_MODELS";

    public static void Attach(UIControlledApplication ui)
    {
        if (Environment.GetEnvironmentVariable(EnvFolder) is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        var log = new SelfTestReporter(Path.Combine(folder, "relatorio.txt"));
        SelfTestDialogs.AutoClose(ui, log);
        ui.ControlledApplication.ApplicationInitialized += (sender, _) =>
        {
            try
            {
                var models = (Environment.GetEnvironmentVariable(EnvModels) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                log.Check("projetos para testar", models.Length > 0, string.Join(", ", models));
                foreach (var model in models)
                {
                    try { RunModel((Application)sender!, model, folder, log); }
                    catch (Exception error) { log.Check($"{Path.GetFileNameWithoutExtension(model)}: projeto aberto e testado", false, error.GetType().Name + ": " + error.Message); }
                }
            }
            catch (Exception error)
            {
                log.Check("excecao nao tratada", false, error.ToString());
            }
            finally
            {
                log.Finish();
            }
        };
    }

    private static void RunModel(Application app, string model, string folder, SelfTestReporter log)
    {
        var name = Path.GetFileNameWithoutExtension(model);
        log.Info($"=== {name}");
        var doc = app.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(model), new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndDiscardWorksets });
        try
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && v.CanBePrinted).ToList();
            var chosen = new List<View3D>();
            // Uma perspectiva com recorte (a camera e comparada com a imagem do Revit) e, se houver, uma sem (tem de ser recusada).
            if (views.FirstOrDefault(v => v.IsPerspective && v.CropBoxActive) is { } perspective) chosen.Add(perspective);
            if (views.FirstOrDefault(v => v.IsPerspective && !v.CropBoxActive) is { } uncropped) chosen.Add(uncropped);
            if (views.FirstOrDefault(v => !v.IsPerspective && v.Name == "{3D}") is { } iso || views.FirstOrDefault(v => !v.IsPerspective) is { } iso2 && (iso = iso2) is not null) chosen.Add(iso!);
            log.Check($"{name}: vistas 3D", chosen.Count > 0, string.Join(", ", chosen.Select(v => v.Name + (v.IsPerspective ? " (perspectiva)" : " (ortografica)"))));

            foreach (var view in chosen)
            {
                var label = $"{name}/{view.Name}";
                if (view.IsPerspective && !view.CropBoxActive)
                {
                    try
                    {
                        SceneExtractor.Extract(doc, view);
                        log.Check($"{label}: perspectiva sem recorte recusada com o motivo", false, "a extracao aceitou");
                    }
                    catch (InvalidOperationException error)
                    {
                        log.Check($"{label}: perspectiva sem recorte recusada com o motivo", error.Message == SceneExtractor.NeedsCropMessage, error.Message);
                    }
                    continue;
                }
                try
                {
                    var started = DateTime.Now;
                    var extracted = SceneExtractor.Extract(doc, view);
                    log.Check($"{label}: geometria extraida", extracted.Scene.TriangleCount > 100,
                        $"{extracted.Scene.TriangleCount:N0} triangulos, {extracted.Scene.Elements.Count:N0} elementos, {extracted.SkippedTransparent:N0} de vidro, {(DateTime.Now - started).TotalSeconds:0.0} s");
                    foreach (var warning in extracted.Warnings) log.Info(warning);
                    log.Info($"{label}: camera {extracted.CameraSource}");

                    var passes = RenderJob.BuildPasses(extracted.Scene, extracted.Camera, RenderQuality.OneK, extracted.Meta, new ManifestOptions { UserPrompt = "autoverificacao" });
                    var verification = ManifestVerifier.Verify(passes.Manifest.Manifest, passes.Layers.Clay, passes.Layers.ColorId, passes.Layers.Depth, passes.Layers.Background);
                    log.Check($"{label}: 4 imagens do mesmo tamanho e 100% das cores no manifesto", verification.Ok,
                        $"{passes.Width}x{passes.Height}, {verification.ColorsInImage} cores na imagem, {verification.ColorsInManifest} no manifesto; {string.Join("; ", verification.Problems.Take(3))}");
                    var covered = passes.Raster.Coverage().Values.Sum() / (double)(passes.Width * passes.Height);
                    log.Check($"{label}: a cena preenche o enquadramento", covered > 0.03, $"{covered:P1} dos pixels com geometria");

                    // A camera esta certa se a caixa projetada de cada elemento grande cruza o que ele ocupa na imagem.
                    var coverage = passes.Raster.Coverage();
                    var checkedCount = 0;
                    var hits = 0;
                    var misses = new List<string>();
                    foreach (var (index, pixels) in coverage.OrderByDescending(c => c.Value).Take(60))
                    {
                        if (pixels < passes.Width * passes.Height * 0.002) break;
                        var element = passes.Raster.Elements[index];
                        if (!extracted.BoundingCorners.TryGetValue(element.Id, out var corners)) continue;
                        if (passes.Raster.BoundsOf(index) is not { } bounds) continue;
                        var projected = Project(extracted.Camera, corners, passes.Width, passes.Height);
                        if (projected is null) continue;
                        checkedCount++;
                        var overlaps = projected.Value.MinX <= bounds.MaxX && projected.Value.MaxX >= bounds.MinX && projected.Value.MinY <= bounds.MaxY && projected.Value.MaxY >= bounds.MinY;
                        if (overlaps) hits++;
                        else misses.Add($"{element.Category} {element.Id}");
                    }
                    log.Check($"{label}: camera coerente com as caixas dos elementos", checkedCount == 0 || hits >= checkedCount * 0.85,
                        $"{hits} de {checkedCount} cruzam; fora: {string.Join(", ", misses.Take(5))}");

                    var target = Path.Combine(folder, name, SafeName(view.Name));
                    CompareWithRevitImage(doc, view, passes, target, label, log);
                    var zip = RenderJob.WritePasses(passes, target);
                    using var stream = File.OpenRead(zip);
                    log.Check($"{label}: pacote render_bundle.zip valido", RenderBundle.Verify(stream).Ok, zip);
                }
                catch (Exception error)
                {
                    log.Check($"{label}: sem excecao da API", false, error.ToString());
                }
            }
        }
        finally
        {
            doc.Close(false);
        }
    }

    /// <summary>
    /// A conferencia de verdade da camera: exporta a mesma vista pelo proprio Revit (ExportImage) e compara o enquadramento, isto e, a
    /// proporcao da imagem e a caixa do que nao e fundo, com a do rasterizador. Vale so com recorte (perspectiva sempre tem); com fundo
    /// que nao e de cor unica (ceu, gradiente) a comparacao e inconclusiva e so vira informacao.
    /// </summary>
    private static void CompareWithRevitImage(Document doc, View3D view, RenderPasses passes, string target, string label, SelfTestReporter log)
    {
        if (!view.IsPerspective && !view.CropBoxActive)
        {
            log.Info($"{label}: sem recorte ativo; a comparacao com a imagem do Revit nao se aplica (enquadramento automatico).");
            return;
        }
        Directory.CreateDirectory(target);
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(target, "revit"),
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ZoomType = ZoomFitType.FitToPage,
            FitDirection = FitDirectionType.Horizontal,
            PixelSize = passes.Width,
            ImageResolution = ImageResolution.DPI_150,
        };
        options.SetViewsAndSheets([view.Id]);
        doc.ExportImage(options);
        var file = Directory.GetFiles(target, "revit*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null || UI.WpfDecoder.Decode(File.ReadAllBytes(file)) is not { } image)
        {
            log.Check($"{label}: imagem do Revit exportada", false, "o ExportImage nao gerou um PNG legivel");
            return;
        }

        var revitAspect = image.Width / (double)image.Height;
        var ourAspect = passes.Width / (double)passes.Height;
        log.Check($"{label}: mesma proporcao da imagem do Revit", Math.Abs(revitAspect / ourAspect - 1) <= 0.03,
            $"Revit {image.Width}x{image.Height} ({revitAspect:0.000}), plugin {passes.Width}x{passes.Height} ({ourAspect:0.000})");

        int Pixel(int x, int y) { var i = (y * image.Width + x) * 3; return (image.Data[i] << 16) | (image.Data[i + 1] << 8) | image.Data[i + 2]; }
        static int Distance(int a, int b) => Math.Max(Math.Abs((a >> 16) - (b >> 16)), Math.Max(Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)), Math.Abs((a & 255) - (b & 255))));
        var background = Pixel(0, 0);
        var corners = new[] { Pixel(image.Width - 1, 0), Pixel(0, image.Height - 1), Pixel(image.Width - 1, image.Height - 1) };
        if (corners.Any(c => Distance(c, background) > 12))
        {
            log.Info($"{label}: fundo da imagem do Revit nao e de cor unica (ceu ou gradiente); comparacao do enquadramento inconclusiva. Ver {file}");
            return;
        }
        // Ignora a moldura de 1% (borda do recorte que o Revit pode desenhar).
        var marginX = Math.Max(2, image.Width / 100);
        var marginY = Math.Max(2, image.Height / 100);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = marginY; y < image.Height - marginY; y++)
            for (var x = marginX; x < image.Width - marginX; x++)
                if (Distance(Pixel(x, y), background) > 24)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        int oMinX = int.MaxValue, oMinY = int.MaxValue, oMaxX = -1, oMaxY = -1;
        for (var y = 0; y < passes.Height; y++)
            for (var x = 0; x < passes.Width; x++)
                if (passes.Raster.IsCovered(x, y))
                {
                    if (x < oMinX) oMinX = x;
                    if (x > oMaxX) oMaxX = x;
                    if (y < oMinY) oMinY = y;
                    if (y > oMaxY) oMaxY = y;
                }
        if (maxX < 0 || oMaxX < 0)
        {
            log.Check($"{label}: enquadramento igual ao do Revit", false, "uma das imagens esta vazia");
            return;
        }
        var revitBox = new[] { minX / (double)image.Width, minY / (double)image.Height, (maxX + 1) / (double)image.Width, (maxY + 1) / (double)image.Height };
        var ourBox = new[] { oMinX / (double)passes.Width, oMinY / (double)passes.Height, (oMaxX + 1) / (double)passes.Width, (oMaxY + 1) / (double)passes.Height };
        var worst = revitBox.Zip(ourBox, (a, b) => Math.Abs(a - b)).Max();
        log.Check($"{label}: enquadramento igual ao do Revit (<= 5% da imagem)", worst <= 0.05,
            $"diferenca maxima {worst:P1}; Revit [{string.Join(", ", revitBox.Select(v => v.ToString("0.000")))}], plugin [{string.Join(", ", ourBox.Select(v => v.ToString("0.000")))}]; {file}");
    }

    private static (int MinX, int MinY, int MaxX, int MaxY)? Project(Camera camera, XYZ[] minMax, int width, int height)
    {
        var (lo, hi) = (minMax[0], minMax[1]);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        var any = false;
        foreach (var x in new[] { lo.X, hi.X })
            foreach (var y in new[] { lo.Y, hi.Y })
                foreach (var z in new[] { lo.Z, hi.Z })
                {
                    var cam = camera.ToCamera(new Vec3(x * SceneExtractor.FtToM, y * SceneExtractor.FtToM, z * SceneExtractor.FtToM));
                    if (!camera.ToPixel(cam, width, height, out var px, out var py)) continue;
                    any = true;
                    minX = Math.Min(minX, (int)px); maxX = Math.Max(maxX, (int)px);
                    minY = Math.Min(minY, (int)py); maxY = Math.Max(maxY, (int)py);
                }
        return any ? (minX, minY, maxX, maxY) : null;
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
