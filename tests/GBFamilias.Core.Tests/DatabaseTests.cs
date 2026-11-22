using Gestaobim.Familias.Core;
using Microsoft.Data.Sqlite;

namespace GBFamilias.Core.Tests;

/// <summary>Le um banco com o mesmo esquema do gbfam e confere a conversao para registros.</summary>
public sealed class DatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gbfam-db-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "gbfam.sqlite");

    public DatabaseTests()
    {
        Directory.CreateDirectory(_dir);
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        Exec(c, """
            CREATE TABLE families (id INTEGER PRIMARY KEY, sha256 TEXT NOT NULL, name TEXT NOT NULL, rel_path TEXT NOT NULL,
                folder_category TEXT, folder_hint TEXT, revit_version TEXT, revit_category TEXT, has_preview INTEGER NOT NULL DEFAULT 0,
                duplicates TEXT NOT NULL DEFAULT '[]', basic_info TEXT, size_bytes INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE previews (family_id INTEGER PRIMARY KEY, png BLOB NOT NULL);
            CREATE TABLE enrichments (family_id INTEGER PRIMARY KEY, data TEXT NOT NULL);
            """);
        Exec(c, """
            INSERT INTO families VALUES (1,'sha1','Sofa 56','#SALA\SOFA\Sofa 56.rfa','SALA','SOFA','2020',NULL,1,'["a","b"]',NULL,4096);
            INSERT INTO families VALUES (2,'sha2','Antiga','#X\Antiga.rfa','X',NULL,NULL,NULL,0,'[]','{"versao_estimada_pelo_build":"2008"}',0);
            INSERT INTO families VALUES (3,'sha3','Quebrada','#X\Q.rfa',NULL,NULL,NULL,NULL,0,'isto nao e json','tambem nao',0);
            INSERT INTO previews VALUES (1, x'89504E47');
            INSERT INTO enrichments VALUES (1, '{"nome_amigavel":"Sofá de três lugares","descricao":"Sofá modular.","categoria":"Mobiliário","subcategoria":"Sofás","tags":["sofá","Sofá","estofado"," "],"materiais":["tecido"],"cores":["cinza"],"estilo":"contemporâneo","ambientes":["sala"]}');
            INSERT INTO enrichments VALUES (3, 'lixo');
            """);
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void LoadsFamiliesWithAiData()
    {
        using var db = LibraryDb.Open(DbPath);
        var index = db.LoadIndex();
        Assert.Equal(3, index.All.Count);

        var sofa = index.ById(1)!;
        Assert.Equal("Sofá de três lugares", sofa.Title);
        Assert.Equal("Mobiliário", sofa.Category);
        Assert.Equal("Sofás", sofa.Subcategory);
        Assert.Equal(["sofá", "estofado"], sofa.Tags); // sem duplicata (ignora caixa) e sem vazio
        Assert.Equal(2, sofa.Copies);
        Assert.Equal(4096, sofa.SizeBytes);
        Assert.True(sofa.HasPreview);
        Assert.True(sofa.Enriched);
        Assert.Equal("2020", sofa.RevitVersion);
        Assert.Same(sofa, index.BySha("SHA1")); // hash sem diferenciar caixa
    }

    [Fact]
    public void EstimatesVersionFromBuildWhenMissing()
    {
        using var db = LibraryDb.Open(DbPath);
        var old = db.LoadIndex().ById(2)!;
        Assert.Equal("~2008", old.RevitVersion);
        Assert.False(old.Enriched);
        Assert.Equal("Antiga", old.Title);
    }

    [Fact]
    public void SurvivesGarbageJsonInOneRow()
    {
        using var db = LibraryDb.Open(DbPath);
        var broken = db.LoadIndex().ById(3)!;
        Assert.Equal("Quebrada", broken.Title);
        Assert.Equal(0, broken.Copies);
        Assert.False(broken.Enriched);
    }

    [Fact]
    public void ReadsPreviewBytesOnDemand()
    {
        using var db = LibraryDb.Open(DbPath);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], db.GetPreview(1));
        Assert.Null(db.GetPreview(2));
    }

    [Fact]
    public void OpenedReadOnlyCannotBeModified()
    {
        using var db = LibraryDb.Open(DbPath);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        c.Open();
        Assert.Throws<SqliteException>(() => Exec(c, "DELETE FROM families"));
    }

    [Fact]
    public void MissingFileGivesAClearError()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => LibraryDb.Open(Path.Combine(_dir, "nao-existe.sqlite")));
        Assert.Contains("nao encontrado", ex.Message);
    }
}

/// <summary>Contra o banco real e o gbfam.exe, quando existem nesta maquina. Sem eles, o teste passa sem fazer nada.</summary>
public class RealLibraryTests
{
    private const string RealDb = @"F:\Projetos\Gestaobim-plugins\banco_familias\gbfam.sqlite";
    private const string RealExe = @"F:\Projetos\Gestaobim-plugins\tools\gbfam\target\release\gbfam.exe";

    [Fact]
    public void LoadsTheRealLibraryAndSearchesInstantly()
    {
        if (!File.Exists(RealDb)) return;
        using var db = LibraryDb.Open(RealDb);
        var index = db.LoadIndex();
        Assert.True(index.All.Count > 3000);
        Assert.Contains(index.All, r => r.Enriched);

        // Veiculo/Veiculos e acentos ja devem ter sido unificados em categorias distintas.
        var cats = index.Counts(new FilterSpec(), Facet.Category);
        Assert.Equal(cats.Count, cats.Select(c => c.Key).Distinct().Count());
        Assert.DoesNotContain(cats, c => c.Label.EndsWith("culo") && cats.Any(o => o != c && o.Label == c.Label + "s"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var found = index.Query(new FilterSpec { Text = "vaso de planta" });
        watch.Stop();
        Assert.NotEmpty(found);
        Assert.True(watch.ElapsedMilliseconds < 500, $"busca textual levou {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task SemanticServerAnswersAndShutsDown()
    {
        if (!File.Exists(RealDb) || !File.Exists(RealExe)) return;
        using var client = await SemanticClient.StartAsync(RealExe, RealDb, TimeSpan.FromSeconds(90));
        Assert.True(client.IsAlive);

        using var db = LibraryDb.Open(RealDb);
        var index = db.LoadIndex();
        var ids = await client.SearchAsync("lavatorio de banheiro", null, null, 5);
        Assert.NotEmpty(ids);
        Assert.Contains(ids, id => index.ById(id)?.Category is "Hidráulica");

        // Restrito a um conjunto de ids: so volta o que esta nele.
        var allowed = index.All.Take(50).Select(r => r.Id).ToHashSet();
        var restricted = await client.SearchAsync("mesa", null, allowed, 10);
        Assert.All(restricted, id => Assert.Contains(id, allowed));

        // Busca por imagem: o preview de uma familia deve achar a propria familia entre as primeiras.
        var withPreview = index.All.First(r => r.HasPreview);
        var viaImage = await client.SearchAsync(null, db.GetPreview(withPreview.Id), null, 5);
        Assert.Contains(withPreview.Id, viaImage);
    }

    [Fact]
    public async Task FailsClearlyWithoutTheExecutable()
    {
        var error = await Assert.ThrowsAsync<SemanticException>(() =>
            SemanticClient.StartAsync(@"C:\nao\existe\gbfam.exe", "x.sqlite", TimeSpan.FromSeconds(2)));
        Assert.Contains("nao encontrado", error.Message);
    }
}
