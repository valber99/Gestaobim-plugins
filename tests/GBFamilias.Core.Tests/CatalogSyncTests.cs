using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Core.Server;
using Microsoft.Data.Sqlite;

namespace GBFamilias.Core.Tests;

/// <summary>Monta catalogos de teste (mesmo formato do gbfam export-catalog) e um "servidor" em memoria.</summary>
internal sealed class CatalogFixture : IDisposable
{
    public const string Schema = """
        CREATE TABLE families (id INTEGER PRIMARY KEY AUTOINCREMENT, sha256 TEXT NOT NULL UNIQUE, path TEXT NOT NULL, rel_path TEXT NOT NULL,
            name TEXT NOT NULL, size_bytes INTEGER NOT NULL, folder_category TEXT, folder_hint TEXT, revit_version TEXT, revit_build TEXT,
            revit_category TEXT, basic_info TEXT, part_atom TEXT, duplicates TEXT NOT NULL DEFAULT '[]', has_preview INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE TABLE previews (family_id INTEGER PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE, png BLOB NOT NULL, width INTEGER, height INTEGER, source TEXT NOT NULL);
        CREATE TABLE enrichments (family_id INTEGER PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE, model TEXT NOT NULL, data TEXT NOT NULL,
            descricao TEXT, categoria TEXT, tags TEXT, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE TABLE embeddings (family_id INTEGER PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE, img BLOB, clip_txt BLOB, desc_vec BLOB,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        """;

    public readonly string Dir = Directory.CreateTempSubdirectory("gbfam-cat").FullName;
    public readonly byte[] Seed = Enumerable.Range(0, 32).Select(i => (byte)(i + 3)).ToArray();
    public string PublicKey => Convert.ToBase64String(Ed25519.PublicKeyFromSeed(Seed));
    public Dictionary<string, byte[]> Files { get; } = [];
    public Func<string, byte[]?>? Intercept { get; set; }
    public int Requests;

    public void Dispose() => Directory.Delete(Dir, true);

    public static string Sha(int n) => $"{n:x64}";

    public string NewDb(string name, params (int n, string desc)[] families)
    {
        var path = Path.Combine(Dir, name);
        if (File.Exists(path)) File.Delete(path);
        using var c = Open(path);
        Exec(c, Schema);
        foreach (var (n, desc) in families) Insert(c, n, desc);
        return path;
    }

    public static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        Exec(c, "PRAGMA foreign_keys = ON");
        return c;
    }

    public static void Exec(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    public static void Insert(SqliteConnection c, int n, string desc)
    {
        Exec(c, "INSERT INTO families (sha256, path, rel_path, name, size_bytes, folder_category, duplicates, has_preview) VALUES ($s, $p, $r, $n, 100, '#PORTAS', '[]', 1)",
            ("$s", Sha(n)), ("$p", $"F:\\x\\{n}.rfa"), ("$r", $"#PORTAS\\porta {n}.rfa"), ("$n", $"Porta {n}"));
        var id = Convert.ToInt64(Scalar(c, "SELECT id FROM families WHERE sha256 = $s", ("$s", Sha(n))));
        Exec(c, "INSERT INTO previews (family_id, png, width, height, source) VALUES ($i, $b, 8, 8, 'embedded')", ("$i", id), ("$b", new byte[] { (byte)n, 1, 2 }));
        Exec(c, "INSERT INTO enrichments (family_id, model, data, descricao, categoria, tags) VALUES ($i, 'm', $d, $de, 'Porta', '[\"a\"]')",
            ("$i", id), ("$d", $"{{\"descricao\":\"{desc}\"}}"), ("$de", desc));
        Exec(c, "INSERT INTO embeddings (family_id, img, clip_txt, desc_vec) VALUES ($i, $b, $b, $b)", ("$i", id), ("$b", new byte[] { (byte)n, 9 }));
    }

    public static object? Scalar(SqliteConnection c, string sql, params (string, object)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        return cmd.ExecuteScalar();
    }

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, true)) gz.Write(data);
        return ms.ToArray();
    }

    /// <summary>Publica um snapshot: o banco com catalog_meta/version, compactado.</summary>
    public FileRef PublishSnapshot(string dbPath, int version)
    {
        var copy = Path.Combine(Dir, $"snap-{version}.sqlite");
        File.Copy(dbPath, copy, true);
        using (var c = Open(copy))
        {
            Exec(c, "CREATE TABLE IF NOT EXISTS catalog_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
            Exec(c, "INSERT OR REPLACE INTO catalog_meta (key, value) VALUES ('version', $v)", ("$v", version.ToString()));
        }
        SqliteConnection.ClearAllPools();
        var gz = Gzip(File.ReadAllBytes(copy));
        var name = $"catalog-{version}.sqlite.gz";
        Files[name] = gz;
        return new FileRef { File = name, Sha256 = CatalogCrypto.Sha256Hex(gz), Bytes = gz.Length };
    }

    public static JsonObject Family(int n, string desc, string? nameOverride = null) => new()
    {
        ["sha256"] = Sha(n), ["path"] = $"F:\\x\\{n}.rfa", ["rel_path"] = $"#PORTAS\\porta {n}.rfa", ["name"] = nameOverride ?? $"Porta {n}",
        ["size_bytes"] = 100, ["folder_category"] = "#PORTAS", ["folder_hint"] = null, ["revit_version"] = null, ["revit_build"] = null,
        ["revit_category"] = null, ["basic_info"] = null, ["part_atom"] = null, ["duplicates"] = "[]", ["has_preview"] = 1, ["created_at"] = "2026-01-01 00:00:00",
    };

    public static JsonObject Bundle(int n, string desc, bool preview = true, bool enrichment = true, bool embedding = true, string? name = null) => new()
    {
        ["family"] = Family(n, desc, name),
        ["preview"] = preview ? new JsonObject { ["png_b64"] = Convert.ToBase64String(new byte[] { (byte)n, 7 }), ["width"] = 8, ["height"] = 8, ["source"] = "embedded" } : null,
        ["enrichment"] = enrichment ? new JsonObject { ["model"] = "m", ["data"] = $"{{\"descricao\":\"{desc}\"}}", ["descricao"] = desc, ["categoria"] = "Porta", ["tags"] = "[\"a\"]", ["created_at"] = "2026-01-01 00:00:00" } : null,
        ["embedding"] = embedding ? new JsonObject { ["img_b64"] = Convert.ToBase64String(new byte[] { (byte)n, 8 }), ["clip_txt_b64"] = null, ["desc_vec_b64"] = null, ["created_at"] = "2026-01-01 00:00:00" } : null,
    };

    public DeltaRef PublishDelta(int from, int to, int familiesAfter, string[] removed, params JsonObject[] upserts)
    {
        var json = new JsonObject
        {
            ["from"] = from, ["to"] = to, ["families_after"] = familiesAfter,
            ["removed"] = new JsonArray(removed.Select(s => (JsonNode)s).ToArray()),
            ["upserts"] = new JsonArray(upserts.Select(u => (JsonNode)u).ToArray()),
        }.ToJsonString();
        var gz = Gzip(Encoding.UTF8.GetBytes(json));
        var name = $"delta-{from}-{to}.json.gz";
        Files[name] = gz;
        return new DeltaRef { From = from, To = to, File = name, Sha256 = CatalogCrypto.Sha256Hex(gz), Bytes = gz.Length };
    }

    public void PublishManifest(int version, int families, FileRef snapshot, IEnumerable<DeltaRef>? deltas = null, string minPlugin = "0.0.1", bool badSignature = false)
    {
        var manifest = new JsonObject
        {
            ["schema"] = 1, ["version"] = version, ["generated_at"] = "2026-01-01T00:00:00Z", ["families"] = families, ["min_plugin"] = minPlugin,
            ["snapshot"] = new JsonObject { ["file"] = snapshot.File, ["sha256"] = snapshot.Sha256, ["bytes"] = snapshot.Bytes },
            ["paths"] = new JsonObject { ["file"] = "paths.json", ["sha256"] = new string('0', 64), ["bytes"] = 0 },
            ["deltas"] = new JsonArray((deltas ?? []).Select(d => (JsonNode)new JsonObject
                { ["from"] = d.From, ["to"] = d.To, ["file"] = d.File, ["sha256"] = d.Sha256, ["bytes"] = d.Bytes }).ToArray()),
        };
        var bytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var sig = Ed25519.Sign(badSignature ? new byte[32] : Seed, bytes);
        Files["manifest.json"] = bytes;
        Files["manifest.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(sig));
    }

    public ServerApi Api()
    {
        var handler = new FakeHandler(r =>
        {
            Interlocked.Increment(ref Requests);
            var name = Uri.UnescapeDataString(r.RequestUri!.AbsolutePath.Replace("/v1/catalog/", ""));
            var data = Intercept?.Invoke(name) ?? (Files.TryGetValue(name, out var f) ? f : null);
            return Task.FromResult(data is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        });
        return new ServerApi(new HttpClient(handler), "https://api.test", (_, _) => Task.FromResult("tk"));
    }

    public CatalogSync Sync(string version = "1.0.0") => new(Api(), PublicKey, Version.Parse(version));

    public static List<string> Shas(string dbPath)
    {
        using var c = Open(dbPath);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT sha256 FROM families ORDER BY sha256";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public static string One(string dbPath, string sql, params (string, object)[] args)
    {
        using var c = Open(dbPath);
        return Convert.ToString(Scalar(c, sql, args)) ?? "";
    }
}

public sealed class CatalogSyncTests : IDisposable
{
    private readonly CatalogFixture _fx = new();
    private string Local => Path.Combine(_fx.Dir, "local", "catalog.sqlite");
    private string Temp => Path.Combine(_fx.Dir, "tmp");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _fx.Dispose();
    }

    private void PublishV1(params (int, string)[] fams)
    {
        var db = _fx.NewDb("v1.sqlite", fams);
        var snap = _fx.PublishSnapshot(db, 1);
        _fx.PublishManifest(1, fams.Length, snap);
    }

    [Fact]
    public async Task PrimeiraSincronizacaoBaixaOSnapshotEInstala()
    {
        PublishV1((1, "a"), (2, "b"), (3, "c"));
        var prepared = await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None);
        Assert.NotNull(prepared);
        Assert.Equal((SyncKind.Snapshot, 1, 3), (prepared!.Kind, prepared.Version, prepared.Families));
        Assert.False(File.Exists(Local)); // so prepara: nao toca no banco em uso
        CatalogSync.Commit(prepared, Local);
        Assert.Equal(1, CatalogSync.LocalVersion(Local));
        Assert.Equal(3, CatalogFixture.Shas(Local).Count);
        Assert.False(File.Exists(prepared.TempPath));
    }

    [Fact]
    public async Task JaAtualizadoNaoBaixaNada()
    {
        PublishV1((1, "a"));
        var first = await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None);
        CatalogSync.Commit(first!, Local);
        _fx.Requests = 0;
        Assert.Null(await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None));
        Assert.Equal(2, _fx.Requests); // so o manifesto e a assinatura
    }

    [Fact]
    public async Task CadeiaDeDeltasAplicaMudancasRemocoesEAlteracoes()
    {
        PublishV1((1, "a"), (2, "b"), (3, "c"));
        CatalogSync.Commit((await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None))!, Local);

        var d12 = _fx.PublishDelta(1, 2, 3, [CatalogFixture.Sha(1)], CatalogFixture.Bundle(4, "nova"));
        var d23 = _fx.PublishDelta(2, 3, 3, [], CatalogFixture.Bundle(2, "b ALTERADA", embedding: false));
        var db3 = _fx.NewDb("v3.sqlite", (2, "b"), (3, "c"), (4, "d"));
        _fx.PublishManifest(3, 3, _fx.PublishSnapshot(db3, 3), [d12, d23]);

        _fx.Files.Remove("catalog-3.sqlite.gz"); // se tentasse o snapshot, falharia: deve usar os deltas
        var prepared = await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None);
        Assert.Equal((SyncKind.Deltas, 3), (prepared!.Kind, prepared.Version));
        CatalogSync.Commit(prepared, Local);

        Assert.Equal(3, CatalogSync.LocalVersion(Local));
        Assert.Equal([CatalogFixture.Sha(2), CatalogFixture.Sha(3), CatalogFixture.Sha(4)], CatalogFixture.Shas(Local));
        Assert.Equal("b ALTERADA", CatalogFixture.One(Local, "SELECT descricao FROM enrichments e JOIN families f ON f.id = e.family_id WHERE f.sha256 = $s", ("$s", CatalogFixture.Sha(2))));
        Assert.Equal("0", CatalogFixture.One(Local, "SELECT count(*) FROM embeddings e JOIN families f ON f.id = e.family_id WHERE f.sha256 = $s", ("$s", CatalogFixture.Sha(2))));
        // a familia removida levou preview, enriquecimento e vetor junto (cascata)
        Assert.Equal("3", CatalogFixture.One(Local, "SELECT count(*) FROM previews"));
        Assert.Equal("1", CatalogFixture.One(Local, "SELECT count(*) FROM families WHERE sha256 = $s AND name = 'Porta 4'", ("$s", CatalogFixture.Sha(4))));
    }

    [Fact]
    public async Task BancoEmUsoNaoEAlteradoAteOCommit()
    {
        PublishV1((1, "a"));
        CatalogSync.Commit((await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None))!, Local);
        var before = File.ReadAllBytes(Local);
        var d = _fx.PublishDelta(1, 2, 2, [], CatalogFixture.Bundle(2, "b"));
        _fx.PublishManifest(2, 2, _fx.PublishSnapshot(_fx.NewDb("v2.sqlite", (1, "a"), (2, "b")), 2), [d]);

        var prepared = await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None);
        Assert.Equal(before, File.ReadAllBytes(Local));
        Assert.Equal(1, CatalogSync.LocalVersion(Local));
        CatalogSync.Commit(prepared!, Local);
        Assert.Equal(2, CatalogSync.LocalVersion(Local));
        Assert.True(File.Exists(Local + ".anterior"));
    }

    [Fact]
    public async Task CadeiaComBuracoCaiNoSnapshot()
    {
        PublishV1((1, "a"));
        CatalogSync.Commit((await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None))!, Local);
        var d34 = _fx.PublishDelta(3, 4, 2, [], CatalogFixture.Bundle(5, "x")); // falta 1->2 e 2->3
        var db4 = _fx.NewDb("v4.sqlite", (1, "a"), (9, "z"));
        _fx.PublishManifest(4, 2, _fx.PublishSnapshot(db4, 4), [d34]);
        var prepared = await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None);
        Assert.Equal(SyncKind.Snapshot, prepared!.Kind);
        CatalogSync.Commit(prepared, Local);
        Assert.Equal([CatalogFixture.Sha(1), CatalogFixture.Sha(9)], CatalogFixture.Shas(Local));
    }

    [Fact]
    public void ConstroiACadeiaDeDeltas()
    {
        static DeltaRef D(int a, int b) => new() { From = a, To = b };
        var m = new CatalogManifest { Version = 4, Deltas = [D(1, 2), D(2, 3), D(3, 4)] };
        Assert.Equal([(1, 2), (2, 3), (3, 4)], CatalogSync.BuildChain(m, 1)!.Select(d => (d.From, d.To)));
        Assert.Equal([(3, 4)], CatalogSync.BuildChain(m, 3)!.Select(d => (d.From, d.To)));
        Assert.Null(CatalogSync.BuildChain(m, 0));
        Assert.Null(CatalogSync.BuildChain(new CatalogManifest { Version = 4, Deltas = [D(1, 2), D(3, 4)] }, 1));
        Assert.Null(CatalogSync.BuildChain(new CatalogManifest { Version = 4, Deltas = [D(1, 1)] }, 1)); // sem progresso: nao entra em laco
        Assert.Null(CatalogSync.BuildChain(new CatalogManifest { Version = 4, Deltas = [D(1, 9)] }, 1)); // passa da versao
    }

    [Fact]
    public async Task VersaoMaisAntigaQueAInstaladaEhRecusada()
    {
        PublishV1((1, "a"));
        CatalogSync.Commit((await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None))!, Local);
        // o servidor (ou um atacante) oferece uma versao menor, assinada validamente
        var d = _fx.PublishDelta(1, 5, 1, []);
        _fx.PublishManifest(5, 1, _fx.PublishSnapshot(_fx.NewDb("v5.sqlite", (1, "a")), 5), [d]);
        CatalogSync.Commit((await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None))!, Local);
        Assert.Equal(5, CatalogSync.LocalVersion(Local));

        PublishV1((1, "a"));
        var error = await Assert.ThrowsAsync<CatalogRejectedException>(() => _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None));
        Assert.Contains("mais antigo", error.Message);
        Assert.Equal(5, CatalogSync.LocalVersion(Local));
    }

    [Fact]
    public async Task ManifestoComAssinaturaRuimEhRecusadoSemBaixarMais()
    {
        var db = _fx.NewDb("v1.sqlite", (1, "a"));
        _fx.PublishManifest(1, 1, _fx.PublishSnapshot(db, 1), badSignature: true);
        var error = await Assert.ThrowsAsync<CatalogRejectedException>(() => _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None));
        Assert.Contains("assinatura", error.Message);
        Assert.Equal(2, _fx.Requests); // manifesto e assinatura; o snapshot nem foi pedido
        Assert.False(File.Exists(Local));
    }

    [Fact]
    public async Task ManifestoAdulteradoDepoisDeAssinadoEhRecusado()
    {
        PublishV1((1, "a"));
        var tampered = Encoding.UTF8.GetString(_fx.Files["manifest.json"]).Replace("\"version\":1", "\"version\":2");
        _fx.Files["manifest.json"] = Encoding.UTF8.GetBytes(tampered);
        await Assert.ThrowsAsync<CatalogRejectedException>(() => _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None));
    }

    [Fact]
    public async Task ArquivoComHashDiferenteDoManifestoEhRecusado()
    {
        PublishV1((1, "a"), (2, "b"));
        var real = _fx.Files["catalog-1.sqlite.gz"];
        _fx.Intercept = name => name == "catalog-1.sqlite.gz" ? CatalogFixture.Gzip("banco falso"u8.ToArray()) : null;
        var error = await Assert.ThrowsAsync<CatalogRejectedException>(() => _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None));
        Assert.Contains("hash", error.Message);
        Assert.False(File.Exists(Local));
        Assert.NotEmpty(real);
    }

    [Fact]
    public async Task DeltaAdulteradoNaoMexeNoBancoLocal()
    {
        PublishV1((1, "a"));
        CatalogSync.Commit((await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None))!, Local);
        var d = _fx.PublishDelta(1, 2, 2, [], CatalogFixture.Bundle(2, "b"));
        _fx.PublishManifest(2, 2, _fx.PublishSnapshot(_fx.NewDb("v2.sqlite", (1, "a"), (2, "b")), 2), [d]);
        _fx.Intercept = name => name == d.File ? CatalogFixture.Gzip("{}"u8.ToArray()) : null;
        await Assert.ThrowsAsync<CatalogRejectedException>(() => _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None));
        Assert.Equal(1, CatalogSync.LocalVersion(Local));
        Assert.Single(CatalogFixture.Shas(Local));
    }

    [Fact]
    public async Task DeltaComContagemInconsistenteEhRecusadoELimpaOTemporario()
    {
        PublishV1((1, "a"));
        CatalogSync.Commit((await _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None))!, Local);
        var d = _fx.PublishDelta(1, 2, 99, [], CatalogFixture.Bundle(2, "b")); // diz 99 familias depois
        _fx.PublishManifest(2, 2, _fx.PublishSnapshot(_fx.NewDb("v2.sqlite", (1, "a"), (2, "b")), 2), [d]);
        await Assert.ThrowsAsync<InvalidDataException>(() => _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(Temp, "catalog.sqlite.next")));
        Assert.Equal(1, CatalogSync.LocalVersion(Local));
    }

    [Fact]
    public async Task SnapshotQueNaoBateComOManifestoEhRecusado()
    {
        var db = _fx.NewDb("v1.sqlite", (1, "a"), (2, "b"));
        var snap = _fx.PublishSnapshot(db, 1);
        _fx.PublishManifest(1, 7, snap); // manifesto diz 7 familias; o banco tem 2
        var error = await Assert.ThrowsAsync<CatalogRejectedException>(() => _fx.Sync().PrepareAsync(Local, Temp, CancellationToken.None));
        Assert.Contains("numero de familias", error.Message);
    }

    [Fact]
    public async Task CatalogoQueExigePluginMaisNovoEhRecusado()
    {
        var db = _fx.NewDb("v1.sqlite", (1, "a"));
        _fx.PublishManifest(1, 1, _fx.PublishSnapshot(db, 1), minPlugin: "9.0.0");
        var error = await Assert.ThrowsAsync<CatalogRejectedException>(() => _fx.Sync("0.2.0").PrepareAsync(Local, Temp, CancellationToken.None));
        Assert.Contains("9.0.0", error.Message);
        // o plugin igual ou mais novo aceita
        Assert.NotNull(await _fx.Sync("9.0.0").PrepareAsync(Local, Temp, CancellationToken.None));
    }

    [Fact]
    public void LocalVersionDeArquivoAusenteOuSemTabelaEhZero()
    {
        Assert.Equal(0, CatalogSync.LocalVersion(Path.Combine(_fx.Dir, "nao-existe.sqlite")));
        Assert.Equal(0, CatalogSync.LocalVersion(_fx.NewDb("sem-meta.sqlite", (1, "a"))));
        var lixo = Path.Combine(_fx.Dir, "lixo.sqlite");
        File.WriteAllText(lixo, "isto nao e um banco sqlite, apenas texto qualquer com mais de cem bytes para parecer um arquivo de verdade........");
        Assert.Equal(0, CatalogSync.LocalVersion(lixo));
    }

    [Fact]
    public async Task UpsertSubstituiPreviewEVetoresEMantemOId()
    {
        var db = _fx.NewDb("a.sqlite", (1, "a"), (2, "b"));
        var idBefore = CatalogFixture.One(db, "SELECT id FROM families WHERE sha256 = $s", ("$s", CatalogFixture.Sha(2)));
        var bytes = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["from"] = 1, ["to"] = 2, ["families_after"] = 2, ["removed"] = new JsonArray(),
            ["upserts"] = new JsonArray(CatalogFixture.Bundle(2, "NOVA", preview: false, embedding: false, name: "Porta 2 renomeada")),
        }.ToJsonString());
        using (var c = CatalogFixture.Open(db)) DeltaApplier.Apply(c, bytes);
        Assert.Equal(idBefore, CatalogFixture.One(db, "SELECT id FROM families WHERE sha256 = $s", ("$s", CatalogFixture.Sha(2))));
        Assert.Equal("Porta 2 renomeada", CatalogFixture.One(db, "SELECT name FROM families WHERE sha256 = $s", ("$s", CatalogFixture.Sha(2))));
        Assert.Equal("0", CatalogFixture.One(db, "SELECT count(*) FROM previews p JOIN families f ON f.id = p.family_id WHERE f.sha256 = $s", ("$s", CatalogFixture.Sha(2))));
        Assert.Equal("NOVA", CatalogFixture.One(db, "SELECT descricao FROM enrichments e JOIN families f ON f.id = e.family_id WHERE f.sha256 = $s", ("$s", CatalogFixture.Sha(2))));
        await Task.CompletedTask;
    }

    [Fact]
    public void DeltaSqlInjectionEmDadosEhTratadoComoTexto()
    {
        var db = _fx.NewDb("a.sqlite", (1, "a"));
        var evil = "'); DROP TABLE families; --";
        var bundle = CatalogFixture.Bundle(2, evil, name: evil);
        bundle["family"]!["rel_path"] = evil;
        var bytes = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["from"] = 1, ["to"] = 2, ["families_after"] = 2, ["removed"] = new JsonArray(evil),
            ["upserts"] = new JsonArray(bundle),
        }.ToJsonString());
        using (var c = CatalogFixture.Open(db)) DeltaApplier.Apply(c, bytes);
        Assert.Equal(2, CatalogFixture.Shas(db).Count);
        Assert.Equal(evil, CatalogFixture.One(db, "SELECT name FROM families WHERE sha256 = $s", ("$s", CatalogFixture.Sha(2))));
    }

    // ---- Rust <-> C#: o catalogo gerado pelo gbfam de verdade, aplicado pelo plugin

    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string GbfamExe = Environment.GetEnvironmentVariable("GBFAM_EXE")
        ?? Path.Combine(RepoRoot, "tools", "gbfam", "target", "release", OperatingSystem.IsWindows() ? "gbfam.exe" : "gbfam");

    private static string Run(string args)
    {
        var info = new ProcessStartInfo(GbfamExe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"gbfam {args}\n{output}");
        return output;
    }

    [Fact]
    public async Task CatalogoGeradoPeloRustEAplicadoPeloPluginComDelta()
    {
        if (!File.Exists(GbfamExe)) return;
        var db = _fx.NewDb("tiny.sqlite", (1, "a"), (2, "b"), (3, "c"));
        var key = Path.Combine(_fx.Dir, "rust.key");
        var generated = Run($"gen-key --out \"{key}\"");
        var pub = generated.Split(':').Last().Trim();
        var outDir = Path.Combine(_fx.Dir, "rust-out");
        SqliteConnection.ClearAllPools();
        Run($"--db \"{db}\" export-catalog --out \"{outDir}\" --key \"{key}\"");

        var api = new ServerApi(new HttpClient(new FakeHandler(r =>
        {
            var name = Uri.UnescapeDataString(r.RequestUri!.AbsolutePath.Replace("/v1/catalog/", ""));
            var file = Path.Combine(outDir, name);
            return Task.FromResult(File.Exists(file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(file)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        })), "https://api.test", (_, _) => Task.FromResult("tk"));
        var sync = new CatalogSync(api, pub, new Version(1, 0, 0));

        CatalogSync.Commit((await sync.PrepareAsync(Local, Temp, CancellationToken.None))!, Local);
        Assert.Equal(1, CatalogSync.LocalVersion(Local));
        Assert.Equal(3, CatalogFixture.Shas(Local).Count);

        // muda o banco do administrador: remove 1, altera 2, acrescenta 4 e 5
        using (var c = CatalogFixture.Open(db))
        {
            CatalogFixture.Exec(c, "DELETE FROM families WHERE sha256 = $s", ("$s", CatalogFixture.Sha(1)));
            CatalogFixture.Exec(c, "UPDATE enrichments SET descricao = 'ALTERADA' WHERE family_id = (SELECT id FROM families WHERE sha256 = $s)", ("$s", CatalogFixture.Sha(2)));
            CatalogFixture.Insert(c, 4, "d");
            CatalogFixture.Insert(c, 5, "e");
        }
        SqliteConnection.ClearAllPools();
        Run($"--db \"{db}\" export-catalog --out \"{outDir}\" --key \"{key}\"");

        var prepared = await sync.PrepareAsync(Local, Temp, CancellationToken.None);
        Assert.Equal((SyncKind.Deltas, 2), (prepared!.Kind, prepared.Version));
        CatalogSync.Commit(prepared, Local);

        Assert.Equal(CatalogFixture.Shas(db), CatalogFixture.Shas(Local));
        Assert.Equal("ALTERADA", CatalogFixture.One(Local, "SELECT descricao FROM enrichments e JOIN families f ON f.id = e.family_id WHERE f.sha256 = $s", ("$s", CatalogFixture.Sha(2))));
        foreach (var n in new[] { 2, 3, 4, 5 })
        {
            var q = "SELECT hex(p.png) || '|' || hex(m.img) FROM families f JOIN previews p ON p.family_id = f.id JOIN embeddings m ON m.family_id = f.id WHERE f.sha256 = $s";
            Assert.Equal(CatalogFixture.One(db, q, ("$s", CatalogFixture.Sha(n))), CatalogFixture.One(Local, q, ("$s", CatalogFixture.Sha(n))));
        }
    }
}
