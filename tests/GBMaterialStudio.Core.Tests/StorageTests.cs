using System.Text.Json.Nodes;
using Gestaobim.MaterialStudio.Core;
using Xunit;

namespace GBMaterialStudio.Core.Tests;

public sealed class TempHome : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gbtest_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("GB_MATERIAL_STUDIO_HOME");

    public TempHome()
    {
        Directory.CreateDirectory(Path);
        Environment.SetEnvironmentVariable("GB_MATERIAL_STUDIO_HOME", Path);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GB_MATERIAL_STUDIO_HOME", _previous);
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

[Collection("uses-environment")]
public class FavoriteStoreTests
{
    private static JsonObject Empty() => new() { ["version"] = 4, ["favorites"] = new JsonArray() };

    private static string[] Backups(string folder) =>
        Directory.GetFiles(folder).Where(f => f.Contains(".bak.")).ToArray();

    private static void AddFavorite(JsonObject db, string id) => ((JsonArray)db["favorites"]!).Add(new JsonObject { ["id"] = id });

    [Fact]
    public void Missing_file_uses_the_default_factory()
    {
        using var home = new TempHome();
        var db = FavoriteStore.Load(Path.Combine(home.Path, "f.json"), Empty);
        Assert.Empty((JsonArray)db["favorites"]!);
    }

    [Fact]
    public void First_mutation_creates_the_file_without_backup()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        FavoriteStore.Mutate(path, Empty, db => { AddFavorite(db, "1"); return 0; });
        Assert.Single((JsonArray)FavoriteStore.Load(path, Empty)["favorites"]!);
        Assert.Empty(Backups(home.Path));
    }

    [Fact]
    public void Second_mutation_keeps_a_backup_of_the_previous_content()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        FavoriteStore.Mutate(path, Empty, db => { AddFavorite(db, "1"); return 0; });
        FavoriteStore.Mutate(path, Empty, db => { AddFavorite(db, "2"); return 0; });
        var backup = Assert.Single(Backups(home.Path));
        var old = JsonNode.Parse(File.ReadAllText(backup))!["favorites"]!.AsArray();
        Assert.Single(old);
        Assert.Equal("1", old[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Corrupt_database_throws_and_is_never_replaced()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        File.WriteAllText(path, "{broken");
        Assert.Throws<StoreException>(() => FavoriteStore.Load(path, Empty));
        Assert.Throws<StoreException>(() => FavoriteStore.Mutate(path, Empty, _ => 0));
        Assert.Equal("{broken", File.ReadAllText(path));
    }

    [Fact]
    public void Newer_version_is_refused()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        File.WriteAllText(path, "{\"version\":5,\"favorites\":[]}");
        Assert.Throws<StoreException>(() => FavoriteStore.Load(path, Empty));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"favorites\":{}}")]
    [InlineData("{\"favorites\":[1]}")]
    [InlineData("{\"favorites\":[],\"version\":-1}")]
    [InlineData("{\"favorites\":[],\"version\":true}")]
    [InlineData("{\"favorites\":[],\"version\":1.5}")]
    [InlineData("{\"favorites\":[{\"x\":NaN}]}")]
    public void Invalid_shapes_are_rejected(string json)
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        File.WriteAllText(path, json);
        Assert.Throws<StoreException>(() => FavoriteStore.Load(path, Empty));
    }

    [Fact]
    public void Utf8_bom_is_accepted()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("{\"version\":3,\"favorites\":[]}")]);
        Assert.Empty((JsonArray)FavoriteStore.Load(path, Empty)["favorites"]!);
    }

    [Fact]
    public void Failing_callback_writes_nothing()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        FavoriteStore.Mutate(path, Empty, db => { AddFavorite(db, "1"); return 0; });
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => FavoriteStore.Mutate<int>(path, Empty, db =>
        {
            AddFavorite(db, "2");
            throw new InvalidOperationException("boom");
        }));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.GetFiles(home.Path), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void Callback_producing_an_invalid_database_is_not_written()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        FavoriteStore.Mutate(path, Empty, _ => 0);
        var before = File.ReadAllBytes(path);
        Assert.Throws<StoreException>(() => FavoriteStore.Mutate(path, Empty, db => { db["favorites"] = "oops"; return 0; }));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Unicode_is_written_unescaped()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        FavoriteStore.Mutate(path, Empty, db => { AddFavorite(db, "Alvenaria Cerâmica"); return 0; });
        Assert.Contains("Alvenaria Cerâmica", File.ReadAllText(path));
    }

    [Fact]
    public void Held_lock_times_out()
    {
        using var home = new TempHome();
        var path = Path.Combine(home.Path, "f.json");
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = Task.Run(() => FavoriteStore.Mutate(path, Empty, db =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return 0;
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var previous = FavoriteStore.LockTimeout;
        FavoriteStore.LockTimeout = TimeSpan.FromMilliseconds(150);
        try
        {
            Assert.Throws<LockTimeoutException>(() => FavoriteStore.Mutate(path, Empty, _ => 0));
        }
        finally
        {
            FavoriteStore.LockTimeout = previous;
            release.Set();
            holder.Wait();
        }
    }
}

[Collection("uses-environment")]
public class SettingsAndMigrationTests
{
    [Fact]
    public void Defaults_when_the_file_is_missing()
    {
        using var home = new TempHome();
        Assert.Equal(home.Path, Settings.LibraryDirectory);
        Assert.Equal("", Settings.ConfiguredLibraryDirectory);
    }

    [Fact]
    public void Saved_library_directory_round_trips_with_unicode()
    {
        using var home = new TempHome();
        Settings.SaveLibraryDirectory(@"C:\Biblioteca Gestão");
        Assert.Equal(@"C:\Biblioteca Gestão", Settings.LibraryDirectory);
    }

    [Fact]
    public void Corrupt_settings_file_gives_defaults_and_is_left_untouched()
    {
        using var home = new TempHome();
        File.WriteAllText(StoragePaths.SettingsFile, "{not json");
        Assert.Equal(home.Path, Settings.LibraryDirectory);
        Assert.Equal("{not json", File.ReadAllText(StoragePaths.SettingsFile));
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        using var home = new TempHome();
        File.WriteAllText(StoragePaths.SettingsFile, "{\"library_dir\":\"X\",\"other\":1}");
        Assert.Equal("X", Settings.LibraryDirectory);
    }
}
