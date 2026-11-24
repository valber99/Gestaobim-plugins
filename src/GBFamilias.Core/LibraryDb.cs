using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Gestaobim.Familias.Core;

/// <summary>
/// Le o gbfam.sqlite (somente leitura). Os metadados vao para um <see cref="LibraryIndex"/> em memoria; os previews
/// (PNG) sao buscados um a um, sob demanda. O host precisa ter chamado SQLitePCL.Batteries_V2.Init() antes.
/// </summary>
public sealed class LibraryDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public string Path { get; }

    private LibraryDb(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
    }

    public static LibraryDb Open(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Banco da biblioteca nao encontrado: {path}", path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            // Sem pool: o pool mantem o arquivo aberto depois do Dispose, e o Windows nao deixa trocar o catalogo.
            Pooling = false,
        }.ToString());
        connection.Open();
        return new LibraryDb(path, connection);
    }

    public LibraryIndex LoadIndex()
    {
        var records = new List<FamilyRecord>();
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT f.id, f.sha256, f.name, f.rel_path, f.folder_category, f.folder_hint,
                       f.revit_version, f.revit_category, f.has_preview, f.duplicates, f.basic_info, e.data, f.size_bytes
                  FROM families f
                  LEFT JOIN enrichments e ON e.family_id = f.id
                 ORDER BY f.id
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read()) records.Add(ReadRecord(r));
        }
        return new LibraryIndex(records);
    }

    private static FamilyRecord ReadRecord(SqliteDataReader r)
    {
        var data = ParseObject(r.IsDBNull(11) ? null : r.GetString(11));
        var basic = ParseObject(r.IsDBNull(10) ? null : r.GetString(10));
        var version = Str(r, 6);
        if (string.IsNullOrWhiteSpace(version) && basic?["versao_estimada_pelo_build"]?.GetValue<string>() is { Length: > 0 } estimated)
            version = "~" + estimated;

        var copies = 0;
        try
        {
            if (JsonNode.Parse(r.IsDBNull(9) ? "[]" : r.GetString(9)) is JsonArray dups) copies = dups.Count;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return new FamilyRecord
        {
            Id = r.GetInt64(0),
            Sha = r.GetString(1),
            Name = r.GetString(2),
            RelPath = r.GetString(3),
            FolderCategory = Str(r, 4),
            FolderHint = Str(r, 5),
            RevitVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            RevitCategory = Str(r, 7),
            HasPreview = !r.IsDBNull(8) && r.GetInt64(8) != 0,
            SizeBytes = r.IsDBNull(12) ? 0 : r.GetInt64(12),
            Copies = copies,
            Enriched = data is not null,
            FriendlyName = Text(data, "nome_amigavel"),
            Description = Text(data, "descricao"),
            Category = Text(data, "categoria"),
            Subcategory = Text(data, "subcategoria"),
            Style = Text(data, "estilo"),
            Tags = List(data, "tags"),
            Materials = List(data, "materiais"),
            Colors = List(data, "cores"),
            Environments = List(data, "ambientes"),
        };
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static IReadOnlyList<string> List(JsonObject? o, string key) =>
        o?[key] is JsonArray a
            ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")
                .Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    private bool _disposed;

    /// <summary>O PNG do preview, ou null se a familia nao tem.</summary>
    public byte[]? GetPreview(long id)
    {
        lock (_lock)
        {
            if (_disposed) return null;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT png FROM previews WHERE family_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }
}
