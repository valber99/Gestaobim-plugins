using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gestaobim.MaterialStudio.Core;

/// <summary>Where the plugin keeps its files. Overridable with GB_MATERIAL_STUDIO_HOME (used by tests).</summary>
public static class StoragePaths
{
    public static string Home =>
        Environment.GetEnvironmentVariable("GB_MATERIAL_STUDIO_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestaoBIM", "MaterialStudio");

    /// <summary>Folder used by the earlier pyRevit edition. Read only: files are copied, never moved.</summary>
    public static string PyRevitHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pyRevit", "MaterialTools");

    public static string SettingsFile => Path.Combine(Home, "gb-settings.json");
    public static string FavoritesFile => Path.Combine(Home, "favorites-v4.json");
    public static string LegacySnapshotFile => Path.Combine(Home, "favorites.json");

    /// <summary>
    /// Copies the favorites, settings and library made by the pyRevit edition into <see cref="Home"/>,
    /// only the files that do not exist there yet. Returns the names copied.
    /// </summary>
    public static IReadOnlyList<string> MigrateFromPyRevit()
    {
        var copied = new List<string>();
        var source = PyRevitHome;
        if (!Directory.Exists(source) || string.Equals(Path.GetFullPath(source), Path.GetFullPath(Home), StringComparison.OrdinalIgnoreCase))
            return copied;
        Directory.CreateDirectory(Home);
        var names = new List<string> { "favorites-v4.json", "favorites.json", "gb-settings.json" };
        names.AddRange(Directory.GetFiles(source, "GBMaterialLibrary-*.rvt").Select(Path.GetFileName)!);
        foreach (var name in names)
        {
            var from = Path.Combine(source, name);
            var to = Path.Combine(Home, name);
            if (!File.Exists(from) || File.Exists(to)) continue;
            File.Copy(from, to);
            copied.Add(name);
        }
        return copied;
    }
}

/// <summary>User settings. An unreadable file is never overwritten: defaults are returned instead.</summary>
public static class Settings
{
    private const string LibraryDirKey = "library_dir";

    public static string LibraryDirectory
    {
        get
        {
            var configured = Read()[LibraryDirKey]?.GetValue<string>()?.Trim();
            return string.IsNullOrEmpty(configured) ? StoragePaths.Home : configured;
        }
    }

    public static string ConfiguredLibraryDirectory => Read()[LibraryDirKey]?.GetValue<string>() ?? "";

    public static void SaveLibraryDirectory(string directory)
    {
        var data = Read();
        data[LibraryDirKey] = directory;
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePaths.SettingsFile)!);
        File.WriteAllText(StoragePaths.SettingsFile, data.ToJsonString(FavoriteStore.WriteOptions), new UTF8Encoding(false));
    }

    private static JsonObject Read()
    {
        try
        {
            var bytes = File.ReadAllBytes(StoragePaths.SettingsFile);
            if (JsonNode.Parse(FavoriteStore.StripBom(bytes)) is JsonObject stored)
            {
                var result = new JsonObject { [LibraryDirKey] = "" };
                if (stored[LibraryDirKey] is JsonValue value && value.TryGetValue<string>(out var text))
                    result[LibraryDirKey] = text;
                return result;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new JsonObject { [LibraryDirKey] = "" };
    }
}

public class StoreException : Exception
{
    public StoreException(string message) : base(message) { }
}

public sealed class LockTimeoutException : IOException
{
    public LockTimeoutException(string message) : base(message) { }
}

/// <summary>
/// Fail-closed JSON favorites persistence. Only a missing file invokes the default factory. Invalid or
/// unreadable databases throw and are never silently replaced. Backups are kept beside the original.
/// Writers coordinate through a named mutex, which Windows releases if a writer crashes.
/// </summary>
public static class FavoriteStore
{
    public const int SupportedVersion = 4;
    public static TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static byte[] StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;

    private static JsonObject Validate(JsonObject db)
    {
        if (db["favorites"] is not JsonArray favorites)
            throw new StoreException("favorites must be a list");
        if (favorites.Any(item => item is not JsonObject))
            throw new StoreException("Each favorite must be an object");
        if (db.ContainsKey("version"))
        {
            if (db["version"] is not JsonValue value || !TryGetInteger(value, out var version) || version < 0)
                throw new StoreException("version must be a nonnegative integer");
            if (version > SupportedVersion)
                throw new StoreException($"Database version is newer than supported version {SupportedVersion}");
        }
        return db;
    }

    /// <summary>
    /// Reads a whole number from a JSON value. Values parsed from a file and values created in code
    /// (an int, say) expose different CLR types, so each is tried; 1.5 and booleans are not integers.
    /// </summary>
    private static bool TryGetInteger(JsonValue value, out long number)
    {
        number = 0;
        if (value.GetValueKind() != JsonValueKind.Number) return false;
        if (value.TryGetValue<long>(out number)) return true;
        if (value.TryGetValue<int>(out var small)) { number = small; return true; }
        if (value.TryGetValue<double>(out var real) && real == Math.Floor(real) && !double.IsInfinity(real))
        {
            number = (long)real;
            return true;
        }
        return false;
    }

    private static JsonObject Decode(byte[] raw)
    {
        try
        {
            if (JsonNode.Parse(StripBom(raw)) is not JsonObject db)
                throw new StoreException("Favorites database must be an object");
            return Validate(db);
        }
        catch (JsonException error)
        {
            throw new StoreException("Invalid favorites JSON: " + error.Message);
        }
    }

    private static (JsonObject Db, byte[]? Raw) ReadFile(string path, Func<JsonObject> defaultFactory)
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return (Validate(defaultFactory()), null);
        }
        catch (DirectoryNotFoundException)
        {
            return (Validate(defaultFactory()), null);
        }
        return (Decode(raw), raw);
    }

    /// <summary>Reads one snapshot. Corruption and future versions throw <see cref="StoreException"/>.</summary>
    public static JsonObject Load(string path, Func<JsonObject> defaultFactory) =>
        ReadFile(Path.GetFullPath(path), defaultFactory).Db;

    /// <summary>
    /// Locks, reads, calls the callback, validates, backs up and atomically publishes.
    /// A callback or serialization failure never writes. The directory is created if missing.
    /// </summary>
    public static T Mutate<T>(string path, Func<JsonObject> defaultFactory, Func<JsonObject, T> callback)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var gate = AcquireLock(path);
        var (db, original) = ReadFile(path, defaultFactory);
        var result = callback(db);
        Validate(db);
        var payload = Encoding.UTF8.GetBytes(db.ToJsonString(WriteOptions));
        Decode(payload);

        var temporary = Path.Combine(Path.GetDirectoryName(path)!, ".favorites-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(payload);
                stream.Flush(true);
            }
            if (original is not null)
                Backup(path, original);
            if (original is not null)
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return result;
    }

    private const int MaxBackups = 5;

    private static void Backup(string path, byte[] raw)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssffffff'Z'");
        var backup = $"{path}.bak.{stamp}.{Guid.NewGuid():N}";
        using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(raw);
            stream.Flush(true);
        }
        PruneBackups(path);
    }

    private static void PruneBackups(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir)) return;
            var backups = Directory.GetFiles(dir, $"{name}.bak.*")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(MaxBackups);
            foreach (var old in backups)
            {
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IDisposable AcquireLock(string path)
    {
        var normalized = Path.GetFullPath(path).ToLowerInvariant();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        var mutex = new Mutex(false, "Local\\ValberFavoriteStore_" + digest);
        bool acquired;
        try
        {
            try
            {
                acquired = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                // Ownership is granted when abandonment is reported. Validation below still fails closed
                // if the last writer crashed mid-write.
                acquired = true;
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        if (!acquired)
        {
            mutex.Dispose();
            throw new LockTimeoutException("Timed out waiting for favorites mutex");
        }
        return new MutexRelease(mutex);
    }

    private sealed class MutexRelease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
    }
}
