using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Gestaobim.MaterialStudio.Core;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>A favorite as the lists show it. <see cref="Data"/> is the stored record.</summary>
public sealed class FavoriteItem(JsonObject data)
{
    public JsonObject Data { get; } = data;
    public string Id => Str("id");
    public string Name => Str("name");
    public string Source => Str("source_doc");
    public IReadOnlyList<string> Warnings { get; set; } = [];

    public string Status =>
        Str("library_uid").Length > 0 ? "na biblioteca"
        : Str("source_key").Length > 0 || Str("project_id").Length > 0 ? "origem nativa"
        : "legado parcial";

    public string Display => $"{Name} | {Source} [{Status}]";

    private string Str(string key) => Data[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>
/// Favorites on top of the JSON store, with the bridge to the portable library. A favorite is matched to a
/// project by the project's internal id, so moving or renaming the file keeps it. Older ones carry id plus path.
/// </summary>
public static class Favorites
{
    private static string DbPath => StoragePaths.FavoritesFile;

    // ---------------------------------------------------------------- helpers

    private static string Str(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static JsonObject DefaultDb()
    {
        if (File.Exists(StoragePaths.LegacySnapshotFile))
            return FavoriteStore.Load(StoragePaths.LegacySnapshotFile,
                () => new JsonObject { ["version"] = 3, ["favorites"] = new JsonArray() });
        return new JsonObject { ["version"] = 4, ["mode"] = "native-reference", ["favorites"] = new JsonArray() };
    }

    public static JsonObject LoadDb() => FavoriteStore.Load(DbPath, DefaultDb);

    private static JsonArray FavoritesOf(JsonObject db) => (JsonArray)db["favorites"]!;

    private static string ProjectId(Document doc) =>
        doc.ProjectInformation?.UniqueId ?? throw new InvalidOperationException("Favoritos portateis exigem um documento de projeto, nao uma familia.");

    private static string LegacySourceKey(Document doc) => $"{ProjectId(doc)}|{(doc.PathName ?? "").ToLowerInvariant()}";

    /// <summary>True when the favorite was saved from this project.</summary>
    private static bool FromDocument(JsonObject item, Document doc)
    {
        var projectId = Str(item, "project_id");
        return projectId.Length > 0 ? projectId == ProjectId(doc) : Str(item, "source_key") == LegacySourceKey(doc);
    }

    private static bool SameSource(JsonObject item, Document doc, Material material) =>
        FromDocument(item, doc) && Str(item, "material_unique_id") == material.UniqueId;

    /// <summary>Name folded to lowercase ASCII with single spaces, the key older records were matched by.</summary>
    public static string NormalizeName(string? value)
    {
        var decomposed = (value ?? "").Normalize(NormalizationForm.FormD);
        var ascii = new StringBuilder();
        foreach (var ch in decomposed)
            if (ch < 128) ascii.Append(ch);
        return System.Text.RegularExpressions.Regex.Replace(ascii.ToString(), @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static JsonObject? ColorJson(Color? color) =>
        color is null || !color.IsValid ? null : new JsonObject { ["r"] = (int)color.Red, ["g"] = (int)color.Green, ["b"] = (int)color.Blue };

    private static string PatternName(Document doc, ElementId id)
    {
        if (!ElementIds.IsValid(id)) return "";
        try { return (doc.GetElement(id) as FillPatternElement)?.GetFillPattern()?.Name ?? ""; }
        catch (Exception) { return ""; }
    }

    private static string Param(Material material, BuiltInParameter parameter) =>
        material.get_Parameter(parameter)?.AsString() ?? "";

    // ---------------------------------------------------------------- serialization

    public static JsonObject Serialize(Document doc, Material material)
    {
        var appearance = MaterialServices.ReadAppearanceEditable(doc, material);
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["name"] = material.Name,
            ["name_key"] = NormalizeName(material.Name),
            ["source_doc"] = doc.Title,
            ["updated_at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["graphics"] = new JsonObject
            {
                ["color"] = ColorJson(material.Color),
                ["transparency"] = material.Transparency,
                ["shininess"] = material.Shininess,
                ["smoothness"] = material.Smoothness,
                ["use_render"] = material.UseRenderAppearanceForShading,
                ["surface_fg_pattern"] = PatternName(doc, material.SurfaceForegroundPatternId),
                ["surface_bg_pattern"] = PatternName(doc, material.SurfaceBackgroundPatternId),
                ["cut_fg_pattern"] = PatternName(doc, material.CutForegroundPatternId),
                ["cut_bg_pattern"] = PatternName(doc, material.CutBackgroundPatternId),
                ["surface_fg_color"] = ColorJson(material.SurfaceForegroundPatternColor),
                ["surface_bg_color"] = ColorJson(material.SurfaceBackgroundPatternColor),
                ["cut_fg_color"] = ColorJson(material.CutForegroundPatternColor),
                ["cut_bg_color"] = ColorJson(material.CutBackgroundPatternColor),
            },
            ["identity"] = new JsonObject
            {
                ["description"] = Param(material, BuiltInParameter.ALL_MODEL_DESCRIPTION),
                ["manufacturer"] = Param(material, BuiltInParameter.ALL_MODEL_MANUFACTURER),
                ["model"] = Param(material, BuiltInParameter.ALL_MODEL_MODEL),
                ["url"] = Param(material, BuiltInParameter.ALL_MODEL_URL),
                ["mark"] = Param(material, BuiltInParameter.ALL_MODEL_MARK),
                ["comments"] = MaterialServices.CommentsParameter(material)?.AsString() ?? "",
                ["material_class"] = material.MaterialClass ?? "",
                ["category"] = material.MaterialCategory ?? "",
            },
            ["appearance"] = new JsonObject
            {
                ["schema"] = appearance.Schema,
                ["diffuse_r"] = appearance.DiffuseR,
                ["diffuse_g"] = appearance.DiffuseG,
                ["diffuse_b"] = appearance.DiffuseB,
                ["roughness"] = appearance.Roughness,
                ["reflectivity"] = appearance.Reflectivity,
                ["glossiness"] = appearance.Glossiness,
                ["transparency"] = appearance.Transparency,
                ["has_texture"] = appearance.HasTexture,
                ["texture_path"] = appearance.MainTexturePath,
            },
            ["project_id"] = ProjectId(doc),
            ["source_key"] = LegacySourceKey(doc),
            ["source_path"] = doc.PathName ?? "",
            ["material_unique_id"] = material.UniqueId,
            ["signature"] = Hygiene.SignatureHash(doc, material),
            ["revit_version"] = doc.Application.VersionNumber,
            ["transfer_mode"] = "native-open-source",
            ["snapshot_only"] = true,
        };
    }

    // ---------------------------------------------------------------- queries

    public static List<FavoriteItem> List(JsonObject? db = null) =>
        FavoritesOf(db ?? LoadDb()).OfType<JsonObject>().Select(d => new FavoriteItem(d))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsFavorite(Material material, JsonObject? db = null) =>
        FavoritesOf(db ?? LoadDb()).OfType<JsonObject>().Any(item => SameSource(item, material.Document, material));

    // ---------------------------------------------------------------- change

    /// <summary>
    /// Saves the material as a favorite and copies it into the portable library. The favorite is saved even when
    /// the library copy fails; the reasons are in the returned item's Warnings.
    /// </summary>
    public static FavoriteItem AddOrUpdate(Application app, Document doc, Material material, bool toLibrary = true)
    {
        var data = Serialize(doc, material);
        var previous = FavoritesOf(LoadDb()).OfType<JsonObject>().FirstOrDefault(old => SameSource(old, doc, material));
        var warnings = new List<string>();
        if (toLibrary)
        {
            try
            {
                var previousUid = Str(previous, "library_uid");
                data["library_uid"] = MaterialLibrary.Push(app, doc, material, previousUid.Length > 0 ? previousUid : null);
            }
            catch (Exception error)
            {
                warnings.Add($"Biblioteca portatil nao atualizada: {error.Message}");
            }
        }

        var item = FavoriteStore.Mutate(DbPath, DefaultDb, db =>
        {
            var favorites = FavoritesOf(db);
            for (var i = 0; i < favorites.Count; i++)
            {
                if (favorites[i] is JsonObject old && SameSource(old, doc, material))
                {
                    data["id"] = Str(old, "id");
                    var merged = (JsonObject)old.DeepClone();
                    foreach (var (key, value) in data.ToList())
                        merged[key] = value?.DeepClone();
                    favorites[i] = merged;
                    data = merged;
                    goto saved;
                }
            }
            favorites.Add(data.DeepClone());
        saved:
            db["version"] = FavoriteStore.SupportedVersion;
            db["mode"] = "native-reference";
            return new FavoriteItem(data);
        });
        item.Warnings = warnings;
        return item;
    }

    /// <summary>Removes a favorite (by material or by record id) and its library copy. Returns warnings.</summary>
    public static IReadOnlyList<string> Remove(Application app, Material? material, string? id)
    {
        var removedUids = new List<string>();
        FavoriteStore.Mutate(DbPath, DefaultDb, db =>
        {
            var favorites = FavoritesOf(db);
            var matches = favorites.OfType<JsonObject>()
                .Where(x => id is not null ? Str(x, "id") == id : material is not null && SameSource(x, material.Document, material))
                .ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException("Identificador ambiguo. Nenhum favorito foi removido.");
            if (matches.Count == 1)
            {
                favorites.Remove(matches[0]);
                var uid = Str(matches[0], "library_uid");
                if (uid.Length > 0) removedUids.Add(uid);
            }
            var stillUsed = favorites.OfType<JsonObject>().Select(x => Str(x, "library_uid")).ToHashSet();
            removedUids.RemoveAll(stillUsed.Contains);
            return 0;
        });

        var warnings = new List<string>();
        if (removedUids.Count > 0)
        {
            try { MaterialLibrary.Remove(app, removedUids); }
            catch (Exception error) { warnings.Add($"A copia na biblioteca nao foi removida: {error.Message}"); }
        }
        return warnings;
    }

    // ---------------------------------------------------------------- transfer

    private static Material? SameProjectMaterial(Document doc, JsonObject favorite)
    {
        if (!FromDocument(favorite, doc)) return null;
        var uid = Str(favorite, "material_unique_id");
        return uid.Length > 0 ? doc.GetElement(uid) as Material : null;
    }

    private static Document? SourceDocument(Application app, JsonObject data)
    {
        foreach (Document candidate in app.Documents)
        {
            if (candidate.IsLinked || candidate.IsFamilyDocument) continue;
            if (FromDocument(data, candidate)) return candidate;
        }
        return null;
    }

    /// <summary>A material already in the project that is the same as the favorite (for example, transferred earlier).</summary>
    private static Material? FindIdentical(Document doc, JsonObject favorite)
    {
        var signature = Str(favorite, "signature");
        var name = Str(favorite, "name");
        if (signature.Length == 0 || name.Length == 0) return null;
        return new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) && Hygiene.SignatureHash(doc, m) == signature);
    }

    /// <summary>
    /// The favorite's material in <paramref name="doc"/>: the project's own, an identical one already there, or a
    /// fresh copy from the source project (if open) or from the library. Never replaces another material.
    /// </summary>
    public static TransferResult GetOrCreateMaterial(Application app, Document doc, JsonObject favorite)
    {
        var existing = SameProjectMaterial(doc, favorite);
        if (existing is not null) return new TransferResult(existing, []);

        var hasOrigin = Str(favorite, "source_key").Length > 0 || Str(favorite, "project_id").Length > 0;
        var libraryUid = Str(favorite, "library_uid");
        if (!hasOrigin && libraryUid.Length == 0)
            throw new InvalidOperationException("Favorito antigo possui apenas snapshot parcial. Abra o projeto de origem e favorite novamente. O registro antigo foi preservado.");

        var identical = FindIdentical(doc, favorite);
        if (identical is not null) return new TransferResult(identical, []);

        var source = hasOrigin ? SourceDocument(app, favorite) : null;
        if (source is not null && source.GetElement(Str(favorite, "material_unique_id")) is Material original)
            return MaterialLibrary.Transfer(source, original, doc);

        if (libraryUid.Length > 0)
            return MaterialLibrary.Pull(app, doc, libraryUid);

        var origin = Str(favorite, "source_path").Length > 0 ? Str(favorite, "source_path") : Str(favorite, "source_doc");
        throw new InvalidOperationException(
            "Abra o projeto de origem no mesmo Revit para transferir o material completo, ou favorite o material novamente " +
            $"para guardar uma copia na biblioteca. Origem: {origin}. Nenhuma copia simplificada foi criada.");
    }

    /// <summary>
    /// Copies favorites that have no library copy yet into the library, when their source project is open.
    /// Returns how many were copied and the error messages.
    /// </summary>
    public static (int Copied, IReadOnlyList<string> Errors) SyncToLibrary(Application app)
    {
        var pending = FavoritesOf(LoadDb()).OfType<JsonObject>()
            .Where(x => Str(x, "library_uid").Length == 0 && (Str(x, "project_id").Length > 0 || Str(x, "source_key").Length > 0))
            .ToList();
        var pushed = new Dictionary<string, string>();
        var errors = new List<string>();
        foreach (var item in pending)
        {
            var source = SourceDocument(app, item);
            if (source is null || source.GetElement(Str(item, "material_unique_id")) is not Material material) continue;
            try
            {
                pushed[Str(item, "id")] = MaterialLibrary.Push(app, source, material);
            }
            catch (Exception error)
            {
                errors.Add($"{Str(item, "name")}: {error.Message}");
            }
        }
        if (pushed.Count > 0)
        {
            FavoriteStore.Mutate(DbPath, DefaultDb, db =>
            {
                foreach (var favorite in FavoritesOf(db).OfType<JsonObject>())
                    if (pushed.TryGetValue(Str(favorite, "id"), out var uid))
                        favorite["library_uid"] = uid;
                return 0;
            });
        }
        return (pushed.Count, errors);
    }
}
