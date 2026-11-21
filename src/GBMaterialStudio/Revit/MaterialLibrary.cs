using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Gestaobim.MaterialStudio.Core;
using Gestaobim.MaterialStudio.UI;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>The library could not be opened, created or updated.</summary>
public sealed class LibraryException(string message) : Exception(message);

/// <summary>A material copied between documents, plus the texture files that could not be found.</summary>
public sealed record TransferResult(Material Material, IReadOnlyList<string> MissingTextures);

/// <summary>
/// The portable material library: a regular Revit project (GBMaterialLibrary-&lt;version&gt;.rvt). A favorite stores the
/// UniqueId of its material in that file, so it can be moved to any project without the source project open.
/// Copies use Revit's native CopyElements, which keeps the full material with its appearance asset.
/// </summary>
public static class MaterialLibrary
{
    private static readonly string[] Dependencies =
    [
        nameof(Material.AppearanceAssetId), nameof(Material.StructuralAssetId), nameof(Material.ThermalAssetId),
        nameof(Material.SurfaceForegroundPatternId), nameof(Material.SurfaceBackgroundPatternId),
        nameof(Material.CutForegroundPatternId), nameof(Material.CutBackgroundPatternId),
    ];

    private sealed class RejectDuplicateTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.Abort;
    }

    public static string LibraryPath(Application app) =>
        Path.Combine(Core.Settings.LibraryDirectory, $"GBMaterialLibrary-{app.VersionNumber}.rvt");

    private static Document? OpenDocument(Application app, string path)
    {
        var target = Path.GetFullPath(path);
        foreach (Document candidate in app.Documents)
        {
            if (candidate.IsLinked || string.IsNullOrEmpty(candidate.PathName)) continue;
            if (string.Equals(Path.GetFullPath(candidate.PathName), target, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    /// <summary>Yields the library document and closes it afterwards, but only if it was opened here.</summary>
    public sealed class Session : IDisposable
    {
        private readonly bool _owned;

        public Document Document { get; }

        internal Session(Application app, bool create)
        {
            var path = LibraryPath(app);
            var existing = OpenDocument(app, path);
            try
            {
                if (existing is not null)
                {
                    Document = existing;
                }
                else if (File.Exists(path))
                {
                    Document = app.OpenDocumentFile(path);
                    _owned = true;
                }
                else if (create)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    Document = app.NewProjectDocument(UnitSystem.Metric);
                    _owned = true;
                    Document.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false });
                }
                else
                {
                    throw new LibraryException($"A biblioteca ainda nao existe: {path}");
                }
            }
            catch (LibraryException)
            {
                throw;
            }
            catch (Exception error)
            {
                if (_owned) Close();
                throw new LibraryException($"Nao foi possivel abrir a biblioteca em {path}: {error.Message}");
            }
        }

        private void Close()
        {
            try { Document?.Close(false); }
            catch (Exception) { }
        }

        public void Dispose()
        {
            if (_owned) Close();
        }
    }

    public static Session Open(Application app, bool create = false) => new(app, create);

    private static HashSet<string> MaterialNames(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).ToHashSet();

    private static Material SingleMaterial(Document target, IEnumerable<ElementId> copied)
    {
        var materials = copied.Select(target.GetElement).OfType<Material>().ToList();
        return materials.Count == 1 ? materials[0] : throw new InvalidOperationException("A copia nao produziu exatamente um material.");
    }

    /// <summary>
    /// Copies a material (with its appearance asset and patterns) between two open documents. Refuses to reuse
    /// or replace a material that already has the same name in the target. Reports texture files that cannot be
    /// found: Revit may still resolve them through its own library paths, so this is a warning, not a failure.
    /// </summary>
    public static TransferResult Transfer(Document source, Material material, Document target)
    {
        if (MaterialNames(target).Contains(material.Name))
            throw new InvalidOperationException(
                $"Ja existe material com o nome {material.Name} no destino. Transferencia cancelada para nao reutilizar ou substituir outro material.");

        var missing = new List<string>();
        if (ElementIds.IsValid(material.AppearanceAssetId) &&
            (source.GetElement(material.AppearanceAssetId) as AppearanceAssetElement)?.GetRenderingAsset() is { } asset)
        {
            foreach (var path in MaterialServices.CollectAssetTextures(asset).Values)
                if (path.Length > 0 && !File.Exists(TexturePaths.Resolve(path)))
                    missing.Add(path);
        }

        using var transaction = new Transaction(target, "Transferir material");
        transaction.Start();
        try
        {
            var options = new CopyPasteOptions();
            options.SetDuplicateTypeNamesHandler(new RejectDuplicateTypes());
            var copied = ElementTransformUtils.CopyElements(source, new List<ElementId> { material.Id }, target, Transform.Identity, options);
            var result = SingleMaterial(target, copied);
            foreach (var property in Dependencies)
            {
                var originalId = (ElementId)typeof(Material).GetProperty(property)!.GetValue(material)!;
                var copiedId = (ElementId)typeof(Material).GetProperty(property)!.GetValue(result)!;
                if (ElementIds.IsValid(originalId) && (!ElementIds.IsValid(copiedId) || target.GetElement(copiedId) is null))
                    throw new InvalidOperationException($"Dependencia nao transferida: {property}");
            }
            transaction.Commit();
            return new TransferResult(result, missing);
        }
        catch (Exception)
        {
            transaction.RollBack();
            throw;
        }
    }

    /// <summary>Copies a material into the library and returns its UniqueId there. previousUid replaces the earlier copy.</summary>
    public static string Push(Application app, Document source, Material material, string? previousUid = null)
    {
        using var session = Open(app, create: true);
        var library = session.Document;
        string uniqueId;
        using (var transaction = new Transaction(library, "Atualizar biblioteca"))
        {
            transaction.Start();
            try
            {
                if (!string.IsNullOrEmpty(previousUid) && library.GetElement(previousUid) is Material old)
                    library.Delete(old.Id);
                if (MaterialNames(library).Contains(material.Name))
                    throw new InvalidOperationException($"Ja existe outro material chamado {material.Name} na biblioteca.");
                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new RejectDuplicateTypes());
                var copied = ElementTransformUtils.CopyElements(source, new List<ElementId> { material.Id }, library, Transform.Identity, options);
                uniqueId = SingleMaterial(library, copied).UniqueId;
                transaction.Commit();
            }
            catch (Exception)
            {
                transaction.RollBack();
                throw;
            }
        }
        try
        {
            library.Save();
        }
        catch (Exception error)
        {
            throw new LibraryException($"Material copiado, mas a biblioteca nao pode ser salva: {error.Message}");
        }
        return uniqueId;
    }

    /// <summary>Copies a library material into <paramref name="target"/>.</summary>
    public static TransferResult Pull(Application app, Document target, string libraryUid)
    {
        using var session = Open(app);
        if (session.Document.GetElement(libraryUid) is not Material material)
            throw new InvalidOperationException("O material nao existe mais na biblioteca.");
        return Transfer(session.Document, material, target);
    }

    /// <summary>Deletes materials from the library. Missing ones are ignored. Returns how many were removed.</summary>
    public static int Remove(Application app, IReadOnlyCollection<string> libraryUids)
    {
        if (libraryUids.Count == 0 || !File.Exists(LibraryPath(app))) return 0;
        var removed = 0;
        using var session = Open(app);
        var library = session.Document;
        using (var transaction = new Transaction(library, "Limpar biblioteca"))
        {
            transaction.Start();
            try
            {
                foreach (var uid in libraryUids)
                {
                    if (library.GetElement(uid) is Material material)
                    {
                        library.Delete(material.Id);
                        removed++;
                    }
                }
                transaction.Commit();
            }
            catch (Exception)
            {
                transaction.RollBack();
                throw;
            }
        }
        if (removed > 0) library.Save();
        return removed;
    }
}
