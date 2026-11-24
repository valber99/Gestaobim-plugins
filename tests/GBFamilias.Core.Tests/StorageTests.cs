using Gestaobim.Familias.Core;

namespace GBFamilias.Core.Tests;

public class PathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "gbfam-lib");

    [Fact]
    public void JoinsBackslashAndSlashPaths()
    {
        var a = FamiliasPaths.ResolveFamilyPath(Root, @"#SALA\Armarios\Sideboard.rfa");
        var b = FamiliasPaths.ResolveFamilyPath(Root, "#SALA/Armarios/Sideboard.rfa");
        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.StartsWith(Path.GetFullPath(Root), a);
        Assert.EndsWith("Sideboard.rfa", a);
    }

    [Theory]
    [InlineData(@"..\fora.rfa")]
    [InlineData(@"sub\..\..\fora.rfa")]
    [InlineData("../../etc/passwd")]
    [InlineData(@"C:\Windows\x.rfa")]
    [InlineData(@"\absoluto.rfa")]
    [InlineData("")]
    public void RejectsPathsOutsideTheLibrary(string relative) =>
        Assert.Null(FamiliasPaths.ResolveFamilyPath(Root, relative));

    [Fact]
    public void RejectsEmptyRoot() => Assert.Null(FamiliasPaths.ResolveFamilyPath("", "a.rfa"));

    [Fact]
    public void AFolderWithTheSamePrefixIsNotInsideTheRoot()
    {
        // "gbfam-lib-outra" comeca com "gbfam-lib" mas e outra pasta.
        Assert.Null(FamiliasPaths.ResolveFamilyPath(Root, @"..\gbfam-lib-outra\x.rfa"));
    }
}

public sealed class UserStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gbfam-user-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "user.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void FavoritesToggleAndPersist()
    {
        var store = new UserStore(File_);
        Assert.True(store.ToggleFavorite("abc"));
        Assert.True(store.ToggleFavorite("def"));
        Assert.False(store.ToggleFavorite("abc"));
        Assert.Equal(["def"], new UserStore(File_).Favorites()); // outra instancia le do arquivo
    }

    [Fact]
    public void CollectionsCreateAddRemoveDelete()
    {
        var store = new UserStore(File_);
        store.CreateCollection("Banheiro");
        store.AddToCollection("Banheiro", "s1");
        store.AddToCollection("Banheiro", "s1"); // sem duplicar
        store.AddToCollection("Cozinha", "s2");  // cria sozinha
        Assert.Equal(["Banheiro", "Cozinha"], store.CollectionNames());
        Assert.Equal(["s1"], store.Collection("Banheiro"));
        store.RemoveFromCollection("Banheiro", "s1");
        Assert.Empty(store.Collection("Banheiro"));
        store.DeleteCollection("Cozinha");
        Assert.Equal(["Banheiro"], store.CollectionNames());
        Assert.Empty(store.Collection("nao existe"));
    }

    [Fact]
    public void CollectionNamesAreCaseInsensitiveAndMustNotBeBlank()
    {
        var store = new UserStore(File_);
        store.CreateCollection("Sala");
        store.AddToCollection("sala", "x");
        Assert.Single(store.CollectionNames());
        Assert.Throws<ArgumentException>(() => store.CreateCollection("   "));
    }

    [Fact]
    public void RecentsAreNewestFirstDeduplicatedAndCapped()
    {
        var store = new UserStore(File_);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.AddRecent("a", t0);
        store.AddRecent("b", t0.AddMinutes(1));
        store.AddRecent("a", t0.AddMinutes(2)); // volta ao topo, sem repetir
        Assert.Equal(["a", "b"], store.Recents().Select(r => r.Sha));

        for (var i = 0; i < UserStore.MaxRecents + 10; i++) store.AddRecent("x" + i, t0.AddHours(1 + i));
        Assert.Equal(UserStore.MaxRecents, store.Recents().Count);
        Assert.Equal("x" + (UserStore.MaxRecents + 9), store.Recents()[0].Sha);
    }

    [Fact]
    public void ClearRecents()
    {
        var store = new UserStore(File_);
        store.AddRecent("a");
        store.ClearRecents();
        Assert.Empty(store.Recents());
    }

    [Fact]
    public void CorruptFileIsKeptAsideNotLost()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ isto nao e json");
        var store = new UserStore(File_);
        Assert.Empty(store.Favorites());           // comeca vazio, sem quebrar
        Assert.True(store.ToggleFavorite("novo")); // e volta a gravar normalmente
        Assert.Single(Directory.GetFiles(_dir, "user.json.corrompido-*")); // o original ficou guardado
    }

    [Fact]
    public void ConcurrentWritersDoNotLoseEachOthersChanges()
    {
        var a = new UserStore(File_);
        var b = new UserStore(File_);
        Parallel.For(0, 40, i => (i % 2 == 0 ? a : b).AddToCollection("c", "s" + i));
        Assert.Equal(40, new UserStore(File_).Collection("c").Count);
    }
}

[Collection("env")]
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gbfam-set-" + Guid.NewGuid().ToString("N"));
    private readonly string? _old = Environment.GetEnvironmentVariable("GB_FAMILIAS_HOME");

    public SettingsTests() => Environment.SetEnvironmentVariable("GB_FAMILIAS_HOME", _dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GB_FAMILIAS_HOME", _old);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void RoundTrips()
    {
        new FamiliasSettings { DownloadsFolder = @"D:\familias", CacheLimitGb = 7, GbfamExe = @"D:\g.exe" }.Save();
        var loaded = FamiliasSettings.Load();
        Assert.Equal(@"D:\familias", loaded.DownloadsFolder);
        Assert.Equal(7, loaded.CacheLimitGb);
        Assert.Equal(@"D:\g.exe", loaded.GbfamExe);
    }

    [Fact]
    public void ConfiguracaoDoInstaladorDaBibliotecaViraAPastaDasFamilias()
    {
        // Saida real do instalador da biblioteca 0.1 (PowerShell ConvertTo-Json): recuo de 4 espacos e "chave":  valor.
        var root = Path.Combine(_dir, "Biblioteca", "familias");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), $$"""
            {
                "library_db":  "{{Path.Combine(_dir, "Biblioteca", "gbfam.sqlite").Replace("\\", "\\\\")}}",
                "library_root":  "{{root.Replace("\\", "\\\\")}}",
                "gbfam_exe":  "",
                "semantic_enabled":  true
            }
            """);
        var s = FamiliasSettings.Load();
        Assert.Equal(root, s.DownloadsFolder); // tudo o que ja tinha continua disponivel, sem baixar de novo
        Assert.Equal("", s.GbfamExe); // vazio = o instalado
        Assert.Equal(0, s.CacheLimitGb);
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var s = FamiliasSettings.Load();
        Assert.Equal("", s.DownloadsFolder);
        Assert.Equal(0, s.CacheLimitGb); // sem limite
        Assert.Equal("", s.GbfamExe);
    }
}

[Collection("env")]
public class ServerSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gbfam-set").FullName;
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("GB_FAMILIAS_HOME");
    private readonly string? _oldLocal = Environment.GetEnvironmentVariable("GB_FAMILIAS_LOCAL");

    public ServerSettingsTests()
    {
        Environment.SetEnvironmentVariable("GB_FAMILIAS_HOME", Path.Combine(_dir, "roaming"));
        Environment.SetEnvironmentVariable("GB_FAMILIAS_LOCAL", Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GB_FAMILIAS_HOME", _oldHome);
        Environment.SetEnvironmentVariable("GB_FAMILIAS_LOCAL", _oldLocal);
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void InstalacaoNovaUsaOServidorSemPastaDefinidaESemLimite()
    {
        var s = FamiliasSettings.Load();
        Assert.Equal("", s.DownloadsFolder);
        Assert.Equal(0, s.CacheLimitGb);
        Assert.Equal(Path.Combine(_dir, "local", "catalog.sqlite"), s.EffectiveDb);
        Assert.Empty(s.Problems()); // a biblioteca abre: o catalogo vem no instalador
        Assert.Contains("pasta", s.DownloadProblem(), StringComparison.OrdinalIgnoreCase); // mas para baixar a pasta e obrigatoria
    }

    [Fact]
    public void ModoLocalDaVersao010MigraParaOServidorComAPastaDaBiblioteca()
    {
        var root = Path.Combine(_dir, "Biblioteca", "familias");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(_dir, "roaming"));
        File.WriteAllText(Path.Combine(_dir, "roaming", "settings.json"),
            $$"""{"library_db":"C:\\Biblioteca\\gbfam.sqlite","library_root":"{{root.Replace("\\", "\\\\")}}","semantic_enabled":false}""");
        var s = FamiliasSettings.Load();
        Assert.Equal(root, s.EffectiveRoot);
        Assert.Equal(FamiliasPaths.CatalogDb, s.EffectiveDb); // o catalogo do instalador, nao o banco antigo
        s.Save();
        var json = File.ReadAllText(Path.Combine(_dir, "roaming", "settings.json"));
        Assert.Contains("\"server\"", json);
        Assert.DoesNotContain("library_root", json);
    }

    [Fact]
    public void MigracaoNaoTrocaUmaPastaJaEscolhidaNemUsaPastaQueNaoExiste()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "roaming"));
        File.WriteAllText(Path.Combine(_dir, "roaming", "settings.json"),
            """{"mode":"local","library_root":"Z:\\nao\\existe"}""");
        Assert.Equal("", FamiliasSettings.Load().DownloadsFolder);
        var chosen = Path.Combine(_dir, "escolhida");
        File.WriteAllText(Path.Combine(_dir, "roaming", "settings.json"),
            $$"""{"mode":"server","library_root":"{{_dir.Replace("\\", "\\\\")}}","downloads_folder":"{{chosen.Replace("\\", "\\\\")}}"}""");
        Assert.Equal(chosen, FamiliasSettings.Load().DownloadsFolder);
    }

    [Fact]
    public void SalvaOsValoresDaTela()
    {
        var downloads = Path.Combine(_dir, "baixadas");
        var s = new FamiliasSettings { DownloadsFolder = downloads, CacheLimitGb = 3, SyncEveryHours = 2 };
        Assert.Empty(s.Problems());
        Assert.Equal(downloads, s.EffectiveRoot);
        s.Save();
        var back = FamiliasSettings.Load();
        Assert.Equal((downloads, 3, 2), (back.DownloadsFolder, back.CacheLimitGb, back.SyncEveryHours));
        Assert.DoesNotContain("gbfam_exe", File.ReadAllText(Path.Combine(_dir, "roaming", "settings.json"))); // nada de caminho do motor na tela nem no arquivo
    }

    [Fact]
    public void ValoresInvalidosNoArquivoViramPadrao()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "roaming"));
        File.WriteAllText(Path.Combine(_dir, "roaming", "settings.json"), "{\"mode\":\"server\",\"cache_limit_gb\":-5,\"sync_every_hours\":0,\"downloads_folder\":\"\"}");
        var s = FamiliasSettings.Load();
        Assert.Equal((0, 6), (s.CacheLimitGb, s.SyncEveryHours));
    }

    [Fact]
    public void AtualizacaoDoCatalogoPeloServidorVemDesligadaEFicaSalva()
    {
        var s = FamiliasSettings.Load();
        Assert.False(s.CatalogSync, "o catalogo vem no instalador; o servidor so entrega os arquivos");
        s.DownloadsFolder = Path.Combine(_dir, "baixadas");
        s.Save();
        Assert.False(FamiliasSettings.Load().CatalogSync);
        s.CatalogSync = true;
        s.Save();
        Assert.True(FamiliasSettings.Load().CatalogSync);
    }

    [Fact]
    public void PastaDeDownloadsRelativaImpedeOUsoEVaziaSoImpedeBaixar()
    {
        Assert.NotEmpty(new FamiliasSettings { DownloadsFolder = "familias" }.Problems());
        var blank = new FamiliasSettings { DownloadsFolder = "  " };
        Assert.Empty(blank.Problems());
        Assert.NotNull(blank.DownloadProblem());
        Assert.Null(new FamiliasSettings { DownloadsFolder = Path.Combine(_dir, "ok") }.DownloadProblem());
    }
}

public class UpdateTests
{
    [Theory]
    [InlineData("v0.2.1", "0.2.1")]
    [InlineData("V1.0.0-beta", "1.0.0")]
    public void ParsesOwnTags(string tag, string expected) =>
        Assert.Equal(UpdateRules.Normalize(Version.Parse(expected)), UpdateRules.ParseVersion(tag));

    [Theory]
    [InlineData("dwg-v0.1.0")]
    [InlineData("familias-v1.0.0")]
    [InlineData("vabc")]
    [InlineData(null)]
    public void IgnoresOtherPluginsTags(string? tag) => Assert.Null(UpdateRules.ParseVersion(tag));

    [Fact]
    public void PicksTheNewestOfOurPluginOnly()
    {
        const string json = """
            [
              {"tag_name":"familias-v9.0.0","html_url":"https://github.com/a/b/releases/1","draft":false,"prerelease":false},
              {"tag_name":"v0.2.0","html_url":"https://github.com/a/b/releases/2","draft":false,"prerelease":false},
              {"tag_name":"v0.3.0","html_url":"https://github.com/a/b/releases/3","draft":true,"prerelease":false},
              {"tag_name":"v0.4.0","html_url":"https://github.com/a/b/releases/4","draft":false,"prerelease":true},
              {"tag_name":"v0.2.5","html_url":"https://evil.example/x","draft":false,"prerelease":false}
            ]
            """;
        var newer = UpdateRules.ParseNewer(json, new Version(0, 1, 0));
        Assert.NotNull(newer);
        Assert.Equal(new Version(0, 2, 0, 0), newer!.Version);
        Assert.Null(UpdateRules.ParseNewer(json, new Version(0, 2, 0)));
        Assert.Null(UpdateRules.ParseNewer("isto nao e json", new Version(0, 1, 0)));
    }

    [Fact]
    public void ShortVersionsAreNormalized() =>
        Assert.Null(UpdateRules.ParseNewer("""[{"tag_name":"v0.1","html_url":"https://github.com/a/b","draft":false,"prerelease":false}]""", new Version(0, 1)));

    [Theory]
    [InlineData("2027", 2027, null)]
    [InlineData("2026", 2027, null)]
    public void RecentVersionsHaveNoNote(string version, int revit, string? expected) =>
        Assert.Equal(expected, FamilyCompat.Note(version, revit));

    [Fact]
    public void OldFamilyWarnsItWillBeUpgraded() =>
        Assert.Contains("atualiza", FamilyCompat.Note("2012", 2027));

    [Fact]
    public void NewerFamilyWarnsItCannotOpen()
    {
        Assert.Contains("nao consegue abri", FamilyCompat.Note("2028", 2027));
        Assert.False(FamilyCompat.CanOpen("2028", 2027));
        Assert.False(FamilyCompat.CanOpen("~2028", 2027));
        Assert.True(FamilyCompat.CanOpen("2027", 2027));
        Assert.True(FamilyCompat.CanOpen("2025", 2027));
        Assert.True(FamilyCompat.CanOpen(null, 2027));
        Assert.True(FamilyCompat.CanOpen("2028", 0));
    }

    [Fact]
    public void EstimatedVersionIsLabeledApproximate() =>
        Assert.Contains("cerca de 2008", FamilyCompat.Note("~2008", 2027));

    [Fact]
    public void UnknownVersionHasNoNote() => Assert.Null(FamilyCompat.Note(null, 2027));

    [Fact]
    public void UnknownRevitYearHasNoNote() => Assert.Null(FamilyCompat.Note("2012", 0));
}

