using System.Globalization;
using System.Text.Json.Nodes;

namespace Gestaobim.Familias.Core;

public sealed record RecentEntry(string Sha, DateTime WhenUtc);

/// <summary>
/// Favoritos, colecoes e recentes do usuario. Ficam num arquivo proprio (a biblioteca e somente leitura e pode ser
/// compartilhada) e usam o hash do conteudo como chave, entao sobrevivem a uma reconstrucao do banco.
/// Cada alteracao le o arquivo de novo dentro de um mutex: duas instancias do Revit nao perdem as mudancas uma da outra.
/// </summary>
public sealed class UserStore
{
    public const int MaxRecents = 60;

    private readonly string _path;
    private static readonly object ProcessLock = new();

    public UserStore(string? path = null) => _path = path ?? Path.Combine(FamiliasPaths.Home, "user-data.json");

    private sealed class Data
    {
        public List<string> Favorites { get; } = [];
        public Dictionary<string, List<string>> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RecentEntry> Recents { get; } = [];
    }

    private Data Read()
    {
        var data = new Data();
        if (FamiliasPaths.ReadJsonOrQuarantine(_path) is not { } root) return data;
        if (root["favorites"] is JsonArray fav)
            foreach (var s in fav.Select(Text).OfType<string>()) if (!data.Favorites.Contains(s)) data.Favorites.Add(s);
        if (root["collections"] is JsonObject cols)
            foreach (var (name, node) in cols)
                if (node is JsonArray items)
                    data.Collections[name] = items.Select(Text).OfType<string>().Distinct().ToList();
        if (root["recents"] is JsonArray rec)
            foreach (var node in rec.OfType<JsonObject>())
                if (Text(node["sha"]) is { } sha
                    && DateTime.TryParse(Text(node["utc"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                    data.Recents.Add(new RecentEntry(sha, when));
        return data;
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private void Write(Data data)
    {
        var root = new JsonObject
        {
            ["favorites"] = new JsonArray(data.Favorites.Select(s => (JsonNode?)s).ToArray()),
            ["collections"] = new JsonObject(data.Collections.Select(kv =>
                KeyValuePair.Create<string, JsonNode?>(kv.Key, new JsonArray(kv.Value.Select(s => (JsonNode?)s).ToArray())))),
            ["recents"] = new JsonArray(data.Recents.Select(r => (JsonNode?)new JsonObject
            {
                ["sha"] = r.Sha,
                ["utc"] = r.WhenUtc.ToString("o", CultureInfo.InvariantCulture),
            }).ToArray()),
        };
        FamiliasPaths.WriteAtomic(_path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private T Locked<T>(Func<Data, T> action, bool save)
    {
        lock (ProcessLock)
        {
            using var mutex = new Mutex(false, @"Local\GBFamiliasUserStore");
            var held = false;
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                held = true; // o dono anterior travou; o arquivo e gravado de forma atomica, entao esta integro
            }
            if (!held)
                throw new IOException("Os dados do usuario estao em uso por outra janela do Revit. Tente novamente em instantes.");
            try
            {
                var data = Read();
                var result = action(data);
                if (save) Write(data);
                return result;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    // ---- Favoritos
    public HashSet<string> Favorites() => Locked(d => new HashSet<string>(d.Favorites), save: false);

    /// <summary>Marca ou desmarca. Devolve true se agora e favorita.</summary>
    public bool ToggleFavorite(string sha) => Locked(d =>
    {
        if (d.Favorites.Remove(sha)) return false;
        d.Favorites.Add(sha);
        return true;
    }, save: true);

    // ---- Colecoes
    public IReadOnlyList<string> CollectionNames() =>
        Locked(d => d.Collections.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList(), save: false);

    public HashSet<string> Collection(string name) =>
        Locked(d => d.Collections.TryGetValue(name, out var items) ? new HashSet<string>(items) : [], save: false);

    public void CreateCollection(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("De um nome para a colecao.");
        Locked(d =>
        {
            if (!d.Collections.ContainsKey(name)) d.Collections[name] = [];
            return 0;
        }, save: true);
    }

    public void DeleteCollection(string name) => Locked(d => d.Collections.Remove(name), save: true);

    public void AddToCollection(string name, string sha) => Locked(d =>
    {
        if (!d.Collections.TryGetValue(name, out var items)) d.Collections[name] = items = [];
        if (!items.Contains(sha)) items.Add(sha);
        return 0;
    }, save: true);

    public void RemoveFromCollection(string name, string sha) =>
        Locked(d => d.Collections.TryGetValue(name, out var items) && items.Remove(sha), save: true);

    // ---- Recentes
    public IReadOnlyList<RecentEntry> Recents() =>
        Locked(d => d.Recents.OrderByDescending(r => r.WhenUtc).ToList(), save: false);

    public void AddRecent(string sha, DateTime? whenUtc = null) => Locked(d =>
    {
        d.Recents.RemoveAll(r => r.Sha == sha);
        d.Recents.Add(new RecentEntry(sha, whenUtc ?? DateTime.UtcNow));
        var keep = d.Recents.OrderByDescending(r => r.WhenUtc).Take(MaxRecents).ToList();
        d.Recents.Clear();
        d.Recents.AddRange(keep);
        return 0;
    }, save: true);

    public void ClearRecents() => Locked(d => { d.Recents.Clear(); return 0; }, save: true);
}
