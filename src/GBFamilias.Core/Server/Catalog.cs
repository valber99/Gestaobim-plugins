using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Gestaobim.Familias.Core.Server;

public sealed class FileRef
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
}

public sealed class DeltaRef
{
    [JsonPropertyName("from")] public int From { get; set; }
    [JsonPropertyName("to")] public int To { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
}

public sealed class CatalogManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("generated_at")] public string GeneratedAt { get; set; } = "";
    [JsonPropertyName("families")] public int Families { get; set; }
    [JsonPropertyName("min_plugin")] public string MinPlugin { get; set; } = "0.0.0";
    [JsonPropertyName("snapshot")] public FileRef Snapshot { get; set; } = new();
    [JsonPropertyName("paths")] public FileRef Paths { get; set; } = new();
    [JsonPropertyName("deltas")] public List<DeltaRef> Deltas { get; set; } = [];

    public static CatalogManifest Parse(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize<CatalogManifest>(json) ?? throw new InvalidDataException("manifesto vazio");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("manifesto ilegivel: " + error.Message);
        }
    }
}

public static class CatalogCrypto
{
    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256HexOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Confere a assinatura do manifesto com a chave publica embutida no plugin.</summary>
    public static bool VerifyManifest(byte[] manifestBytes, string signatureBase64, string publicKeyBase64)
    {
        try
        {
            return Ed25519.Verify(Convert.FromBase64String(publicKeyBase64.Trim()), manifestBytes, Convert.FromBase64String(signatureBase64.Trim()));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Aplica um delta (dados JSON, nunca SQL vindo da rede) com comandos parametrizados, numa transacao so.</summary>
public static class DeltaApplier
{
    public static void Apply(SqliteConnection connection, byte[] deltaJson)
    {
        var root = JsonNode.Parse(deltaJson) as JsonObject ?? throw new InvalidDataException("delta ilegivel");
        var to = root["to"]!.GetValue<int>();
        var expected = root["families_after"]!.GetValue<int>();

        using var tx = connection.BeginTransaction();
        Exec(connection, tx, "PRAGMA defer_foreign_keys = ON");
        foreach (var sha in (root["removed"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
            Exec(connection, tx, "DELETE FROM families WHERE sha256 = $sha", ("$sha", sha));

        foreach (var item in (root["upserts"] as JsonArray ?? []).Select(n => (JsonObject)n!))
        {
            var f = (JsonObject)item["family"]!;
            Exec(connection, tx, """
                INSERT INTO families (sha256, path, rel_path, name, size_bytes, folder_category, folder_hint,
                    revit_version, revit_build, revit_category, basic_info, part_atom, duplicates, has_preview, created_at)
                VALUES ($sha,$path,$rel,$name,$size,$fc,$fh,$rv,$rb,$rc,$bi,$pa,$dup,$hp,$ca)
                ON CONFLICT(sha256) DO UPDATE SET path=excluded.path, rel_path=excluded.rel_path, name=excluded.name,
                    size_bytes=excluded.size_bytes, folder_category=excluded.folder_category, folder_hint=excluded.folder_hint,
                    revit_version=excluded.revit_version, revit_build=excluded.revit_build, revit_category=excluded.revit_category,
                    basic_info=excluded.basic_info, part_atom=excluded.part_atom, duplicates=excluded.duplicates,
                    has_preview=excluded.has_preview, created_at=excluded.created_at
                """,
                ("$sha", S(f, "sha256")), ("$path", S(f, "path")), ("$rel", S(f, "rel_path")), ("$name", S(f, "name")),
                ("$size", f["size_bytes"]!.GetValue<long>()), ("$fc", S(f, "folder_category")), ("$fh", S(f, "folder_hint")),
                ("$rv", S(f, "revit_version")), ("$rb", S(f, "revit_build")), ("$rc", S(f, "revit_category")),
                ("$bi", S(f, "basic_info")), ("$pa", S(f, "part_atom")), ("$dup", S(f, "duplicates") ?? "[]"),
                ("$hp", f["has_preview"]!.GetValue<long>()), ("$ca", S(f, "created_at")));

            var id = Convert.ToInt64(Scalar(connection, tx, "SELECT id FROM families WHERE sha256 = $sha", ("$sha", S(f, "sha256"))));
            Exec(connection, tx, "DELETE FROM previews WHERE family_id = $id", ("$id", id));
            Exec(connection, tx, "DELETE FROM enrichments WHERE family_id = $id", ("$id", id));
            Exec(connection, tx, "DELETE FROM embeddings WHERE family_id = $id", ("$id", id));

            if (item["preview"] is JsonObject p)
                Exec(connection, tx, "INSERT INTO previews (family_id, png, width, height, source) VALUES ($id,$png,$w,$h,$src)",
                    ("$id", id), ("$png", Convert.FromBase64String(S(p, "png_b64")!)), ("$w", L(p, "width")), ("$h", L(p, "height")), ("$src", S(p, "source")));
            if (item["enrichment"] is JsonObject e)
                Exec(connection, tx, "INSERT INTO enrichments (family_id, model, data, descricao, categoria, tags, created_at) VALUES ($id,$m,$d,$de,$c,$t,$ca)",
                    ("$id", id), ("$m", S(e, "model")), ("$d", S(e, "data")), ("$de", S(e, "descricao")), ("$c", S(e, "categoria")), ("$t", S(e, "tags")), ("$ca", S(e, "created_at")));
            if (item["embedding"] is JsonObject m)
                Exec(connection, tx, "INSERT INTO embeddings (family_id, img, clip_txt, desc_vec, created_at) VALUES ($id,$i,$c,$d,$ca)",
                    ("$id", id), ("$i", B(m, "img_b64")), ("$c", B(m, "clip_txt_b64")), ("$d", B(m, "desc_vec_b64")), ("$ca", S(m, "created_at")));
        }

        var count = Convert.ToInt32(Scalar(connection, tx, "SELECT count(*) FROM families"));
        if (count != expected) throw new InvalidDataException($"delta inconsistente: {count} familias depois de aplicar, esperado {expected}");
        Exec(connection, tx, "CREATE TABLE IF NOT EXISTS catalog_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Exec(connection, tx, "INSERT INTO catalog_meta (key, value) VALUES ('version', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$v", to.ToString()));
        tx.Commit();
    }

    private static string? S(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static object L(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : DBNull.Value;
    private static object B(JsonObject o, string key) => S(o, key) is { } s ? Convert.FromBase64String(s) : DBNull.Value;

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction tx, string sql, (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteScalar();
    }
}

public enum SyncKind
{
    UpToDate,
    Snapshot,
    Deltas,
}

public sealed record PreparedCatalog(string TempPath, int Version, SyncKind Kind, int Families);

public sealed class CatalogRejectedException(string message) : Exception(message);

/// <summary>
/// Sincroniza o catalogo local com o servidor sem nunca tocar no banco em uso: baixa, confere assinatura e hashes,
/// aplica num arquivo temporario, valida, e so entao o chamador troca o arquivo (com o banco fechado).
/// </summary>
public sealed class CatalogSync(ServerApi api, string publicKeyBase64, Version pluginVersion)
{
    /// <summary>A versao instalada do catalogo (0 se nao ha banco ou ele nao tem a tabela de versao).</summary>
    public static int LocalVersion(string dbPath)
    {
        if (!File.Exists(dbPath)) return 0;
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM catalog_meta WHERE key = 'version'";
            return int.TryParse(cmd.ExecuteScalar() as string, out var v) ? v : 0;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    public async Task<CatalogManifest> FetchManifestAsync(CancellationToken cancel)
    {
        var json = await api.GetCatalogFileAsync("manifest.json", cancel).ConfigureAwait(false);
        var signature = Encoding.ASCII.GetString(await api.GetCatalogFileAsync("manifest.sig", cancel).ConfigureAwait(false));
        if (!CatalogCrypto.VerifyManifest(json, signature, publicKeyBase64))
            throw new CatalogRejectedException("A assinatura do catalogo NAO confere. Ele foi recusado por seguranca.");
        return CatalogManifest.Parse(json);
    }

    /// <summary>Prepara a atualizacao em <paramref name="tempDir"/>. Devolve null se ja esta atualizado.</summary>
    public async Task<PreparedCatalog?> PrepareAsync(string localDbPath, string tempDir, CancellationToken cancel)
    {
        var manifest = await FetchManifestAsync(cancel).ConfigureAwait(false);
        var local = LocalVersion(localDbPath);

        if (manifest.Version < local)
            throw new CatalogRejectedException($"O servidor ofereceu o catalogo {manifest.Version}, mais antigo que o instalado ({local}). Recusado.");
        if (manifest.Version == local) return null;
        if (Version.TryParse(manifest.MinPlugin, out var min) && min > pluginVersion)
            throw new CatalogRejectedException($"Este catalogo exige o plugin {manifest.MinPlugin} ou mais novo (o seu e {pluginVersion}). Atualize o plugin.");

        Directory.CreateDirectory(tempDir);
        var chain = local > 0 ? BuildChain(manifest, local) : null;
        return chain is not null
            ? await ApplyDeltasAsync(manifest, chain, localDbPath, tempDir, cancel).ConfigureAwait(false)
            : await DownloadSnapshotAsync(manifest, tempDir, cancel).ConfigureAwait(false);
    }

    /// <summary>Deltas consecutivos de <paramref name="local"/> ate a versao do manifesto, ou null se a cadeia tem buraco.</summary>
    public static List<DeltaRef>? BuildChain(CatalogManifest manifest, int local)
    {
        var chain = new List<DeltaRef>();
        var at = local;
        while (at < manifest.Version)
        {
            var next = manifest.Deltas.FirstOrDefault(d => d.From == at);
            if (next is null || next.To <= at) return null;
            chain.Add(next);
            at = next.To;
        }
        return at == manifest.Version ? chain : null;
    }

    private async Task<byte[]> DownloadChecked(FileRef file, CancellationToken cancel) =>
        await DownloadChecked(file.File, file.Sha256, cancel).ConfigureAwait(false);

    private async Task<byte[]> DownloadChecked(string name, string sha, CancellationToken cancel)
    {
        var data = await api.GetCatalogFileAsync(name, cancel).ConfigureAwait(false);
        if (!string.Equals(CatalogCrypto.Sha256Hex(data), sha, StringComparison.OrdinalIgnoreCase))
            throw new CatalogRejectedException($"O arquivo {name} nao confere com o manifesto assinado (hash diferente). Recusado.");
        return data;
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private async Task<PreparedCatalog> DownloadSnapshotAsync(CatalogManifest manifest, string tempDir, CancellationToken cancel)
    {
        var gzBytes = await DownloadChecked(manifest.Snapshot, cancel).ConfigureAwait(false);
        var tmp = Path.Combine(tempDir, $"catalog.sqlite.{Guid.NewGuid():N}.tmp");
        var path = Path.Combine(tempDir, "catalog.sqlite.next");
        try
        {
            using (var input = new GZipStream(new MemoryStream(gzBytes), CompressionMode.Decompress))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await input.CopyToAsync(output, cancel).ConfigureAwait(false);
            }
            Validate(tmp, manifest);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        return new PreparedCatalog(path, manifest.Version, SyncKind.Snapshot, manifest.Families);
    }

    private async Task<PreparedCatalog> ApplyDeltasAsync(CatalogManifest manifest, List<DeltaRef> chain, string localDb, string tempDir, CancellationToken cancel)
    {
        var deltas = new List<byte[]>();
        foreach (var d in chain) deltas.Add(Gunzip(await DownloadChecked(d.File, d.Sha256, cancel).ConfigureAwait(false)));

        var path = Path.Combine(tempDir, "catalog.sqlite.next");
        CopyDatabase(localDb, path);
        try
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                c.Open();
                using (var pragma = c.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = DELETE;";
                    pragma.ExecuteNonQuery();
                }
                foreach (var delta in deltas) DeltaApplier.Apply(c, delta);
            }
            Validate(path, manifest);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
        return new PreparedCatalog(path, manifest.Version, SyncKind.Deltas, manifest.Families);
    }

    /// <summary>Copia o banco de forma consistente mesmo com o original aberto por outro processo (VACUUM INTO).</summary>
    private static void CopyDatabase(string from, string to)
    {
        TryDelete(to);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = from, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $to";
        cmd.Parameters.AddWithValue("$to", to);
        cmd.ExecuteNonQuery();
    }

    /// <summary>O arquivo preparado precisa abrir, passar na checagem de integridade e bater com o manifesto.</summary>
    public static void Validate(string path, CatalogManifest manifest)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        string One(string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        if (One("PRAGMA quick_check") != "ok") throw new CatalogRejectedException("O catalogo baixado esta corrompido.");
        if (One("SELECT count(*) FROM families") != manifest.Families.ToString())
            throw new CatalogRejectedException("O catalogo baixado nao tem o numero de familias do manifesto.");
        if (One("SELECT value FROM catalog_meta WHERE key = 'version'") != manifest.Version.ToString())
            throw new CatalogRejectedException("A versao dentro do catalogo nao e a do manifesto.");
    }

    /// <summary>Troca o banco local pelo preparado. O chamador garante que nenhum leitor esta com o banco aberto.</summary>
    public static void Commit(PreparedCatalog prepared, string localDbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localDbPath)!);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) TryDelete(localDbPath + suffix);
        if (File.Exists(localDbPath)) File.Replace(prepared.TempPath, localDbPath, localDbPath + ".anterior", ignoreMetadataErrors: true);
        else File.Move(prepared.TempPath, localDbPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
