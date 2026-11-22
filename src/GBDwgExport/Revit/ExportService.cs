using Autodesk.Revit.DB;
using Gestaobim.DwgExport.Bridge;
using Gestaobim.DwgExport.Core;

namespace Gestaobim.DwgExport.Revit;

public sealed class ExportException(string message) : Exception(message);

public sealed record ExportRequest(
    IReadOnlyList<ExportItem> Items,
    OutputMode Mode,
    bool Dxf,
    NamePattern Pattern,
    string Folder,
    string RevitSetup,
    string ProjectName)
{
    public string Extension => Dxf ? ".dxf" : ".dwg";
}

/// <summary>What will be written, decided before anything is exported so the user can be asked about conflicts.</summary>
public sealed record ExportPlan(
    IReadOnlyList<(ExportItem Item, string FileName)> Files,
    string MergedFile,
    string IndividualFolder,
    IReadOnlyList<string> PathsTooLong,
    IReadOnlyList<string> Existing);

public sealed class ExportReport
{
    public List<string> Written { get; } = [];
    public List<string> Failures { get; } = [];
    public List<string> Losses { get; } = [];
    public string? MergedFile { get; set; }
    public CadcraftOutput.Summary? MergedSummary { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>Exports through Revit's own engine and, when asked, merges the result with the bridge.</summary>
public static class ExportService
{
    public static ExportPlan Plan(ExportRequest request)
    {
        var used = new HashSet<string>();
        var files = request.Items
            .Select(item => (Item: item, FileName: FileNamer.Unique(
                item.IsSheet ? FileNamer.ForSheet(item.Number, item.Name, request.Pattern) : FileNamer.Sanitize(item.Name), used)))
            .ToList();

        var folder = request.Folder;
        var projectName = FileNamer.Sanitize(request.ProjectName);
        var merged = Path.Combine(folder, projectName + request.Extension);
        var individual = Path.Combine(folder, projectName + " - individuais");

        var merging = request.Mode != OutputMode.SeparateFiles;
        var names = files.Select(file => file.FileName).ToList();
        // Merging writes one file; the individual files only live in a work folder, so only the merged path matters.
        var tooLong = merging
            ? FileNamer.TooLong(folder, [projectName], request.Extension).ToList()
            : FileNamer.TooLong(folder, names, request.Extension).ToList();

        var existing = new List<string>();
        if (merging)
        {
            if (File.Exists(merged)) existing.Add(merged);
        }
        else
        {
            existing.AddRange(names.Select(name => Path.Combine(folder, name + request.Extension)).Where(File.Exists));
        }

        return new ExportPlan(files, merged, individual, tooLong, existing);
    }

    public static ExportReport Run(Document doc, ExportRequest request, ExportPlan plan, Action<string>? progress = null)
    {
        if (request.Items.Count == 0) throw new ExportException("Nenhuma folha ou vista selecionada.");
        var started = DateTime.Now;
        var report = new ExportReport();
        var merging = request.Mode != OutputMode.SeparateFiles;

        // Merging needs the files anyway: they go to a work folder, and are copied out only if the user wants them kept.
        var workFolder = merging
            ? Path.Combine(Path.GetTempPath(), "GBDwgExport", Guid.NewGuid().ToString("N"))
            : request.Folder;
        Directory.CreateDirectory(workFolder);

        try
        {
            var exported = new List<CadcraftBridge.MergeInput>();
            var index = 0;
            foreach (var (item, fileName) in plan.Files)
            {
                index++;
                var label = item.IsSheet ? $"{item.Number} - {item.Name}" : item.Name;
                progress?.Invoke($"Exportando {index}/{plan.Files.Count}: {label}");
                try
                {
                    ExportOne(doc, request, item, workFolder, fileName);
                    var file = Path.Combine(workFolder, fileName + request.Extension);
                    if (!File.Exists(file)) throw new ExportException("o Revit nao gerou o arquivo");
                    exported.Add(new CadcraftBridge.MergeInput(label, file, item.PaperWidthMm, item.PaperHeightMm,
                        item.OriginXMm, item.OriginYMm, item.IsSheet));
                    if (!merging) report.Written.Add(file);
                }
                catch (Exception error)
                {
                    report.Failures.Add($"{label}: {error.Message}");
                }
            }

            if (merging && exported.Count > 0)
            {
                progress?.Invoke(request.Items.Any(item => item.IsSheet) ? "Juntando as folhas num so desenho..." : "Juntando as vistas num so desenho...");
                try
                {
                    var merge = CadcraftBridge.Merge(exported,
                        new MergeOptions(request.Mode, LayoutName: FileNamer.Sanitize(request.ProjectName)),
                        plan.MergedFile);
                    report.MergedFile = merge.File;
                    report.MergedSummary = merge.Summary;
                    report.Losses.AddRange(merge.Losses);
                    report.Written.Add(merge.File);
                    CopyImages(workFolder, request.Folder, report);
                }
                catch (BridgeException error)
                {
                    report.Failures.Add(error.Message);
                }

                // When the merge failed the individual files are all the user has, so they are kept instead of discarded.
                if (report.MergedFile is null)
                {
                    report.Failures.Add($"Os arquivos individuais exportados pelo Revit foram guardados em: {plan.IndividualFolder}");
                    Directory.CreateDirectory(plan.IndividualFolder);
                    foreach (var input in exported)
                    {
                        var target = Path.Combine(plan.IndividualFolder, Path.GetFileName(input.File));
                        File.Copy(input.File, target, overwrite: true);
                        report.Written.Add(target);
                    }
                }
            }
        }
        finally
        {
            if (merging)
            {
                try { Directory.Delete(workFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        report.Elapsed = DateTime.Now - started;
        return report;
    }

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"];

    /// <summary>
    /// Revit writes the images of a sheet (logos, renders) as files beside the DWG, which refers to them by name.
    /// The merged drawing keeps those references, so the images go to the output folder with it.
    /// </summary>
    private static void CopyImages(string workFolder, string folder, ExportReport report)
    {
        foreach (var image in Directory.EnumerateFiles(workFolder)
                     .Where(file => ImageExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)))
        {
            var target = Path.Combine(folder, Path.GetFileName(image));
            try
            {
                File.Copy(image, target, overwrite: true);
                report.Written.Add(target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report.Failures.Add($"Imagem {Path.GetFileName(image)}: {error.Message}");
            }
        }
    }

    private static void ExportOne(Document doc, ExportRequest request, ExportItem item, string folder, string fileName)
    {
        var target = Path.Combine(folder, fileName + request.Extension);
        var plotConfig = Path.Combine(folder, fileName + ".pcp");
        var hadPlotConfig = File.Exists(plotConfig);

        // The user already agreed to overwrite (the plan asked), so the old file goes first instead of trusting how Revit treats it.
        if (File.Exists(target)) File.Delete(target);

        ICollection<ElementId> views = [item.Id];
        var ok = request.Dxf
            ? doc.Export(folder, fileName, views, DxfOptions(doc, request.RevitSetup))
            : doc.Export(folder, fileName, views, DwgOptions(doc, request.RevitSetup, merged: request.Mode != OutputMode.SeparateFiles));

        // Revit writes a plot-settings .pcp beside every export. It is clutter unless the user already had one.
        if (!hadPlotConfig && File.Exists(plotConfig))
        {
            try { File.Delete(plotConfig); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (!ok) throw new ExportException("o Revit recusou a exportacao");
    }

    /// <summary>
    /// The project's own export setup when one is chosen, else sensible defaults. When the files are going to be merged,
    /// views are always written into the sheet's model space: a setup that exports them as external references
    /// (MergedViews off) writes each view to its own file, which the merge would leave behind in the work folder.
    /// </summary>
    public static DWGExportOptions DwgOptions(Document doc, string setup, bool merged = false)
    {
        if (!string.IsNullOrEmpty(setup))
        {
            var options = DWGExportOptions.GetPredefinedOptions(doc, setup);
            if (options is not null)
            {
                if (merged) options.MergedViews = true;
                return options;
            }
        }
        return new DWGExportOptions
        {
            MergedViews = true,
            HideReferencePlane = true,
            HideScopeBox = true,
            HideUnreferenceViewTags = true,
            Colors = ExportColorMode.IndexColors,
            ExportOfSolids = SolidGeometry.Polymesh,
            TargetUnit = ProjectUnit(doc),
        };
    }

    public static DXFExportOptions DxfOptions(Document doc, string setup)
    {
        if (!string.IsNullOrEmpty(setup))
        {
            var options = DXFExportOptions.GetPredefinedOptions(doc, setup);
            if (options is not null) return options;
        }
        return new DXFExportOptions
        {
            HideReferencePlane = true,
            HideScopeBox = true,
            HideUnreferenceViewTags = true,
            Colors = ExportColorMode.IndexColors,
            TargetUnit = ProjectUnit(doc),
        };
    }

    private static ExportUnit ProjectUnit(Document doc)
    {
        try
        {
            var unit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
            if (unit == UnitTypeId.Millimeters) return ExportUnit.Millimeter;
            if (unit == UnitTypeId.Centimeters) return ExportUnit.Centimeter;
            if (unit == UnitTypeId.Meters) return ExportUnit.Meter;
            if (unit == UnitTypeId.Inches || unit == UnitTypeId.FractionalInches) return ExportUnit.Inch;
            if (unit == UnitTypeId.Feet || unit == UnitTypeId.FeetFractionalInches) return ExportUnit.Foot;
        }
        catch (Exception)
        {
            // Fall through to the default below.
        }
        return doc.DisplayUnitSystem == DisplayUnit.METRIC ? ExportUnit.Millimeter : ExportUnit.Inch;
    }
}
