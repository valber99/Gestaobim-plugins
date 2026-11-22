using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Core.Server;

namespace Gestaobim.Familias.Revit;

/// <summary>O catalogo ainda nao foi baixado do servidor (primeiro uso): a interface faz o login e baixa.</summary>
public sealed class CatalogMissingException(string message) : Exception(message);

public enum SemanticState
{
    Off,
    Starting,
    Ready,
    Unavailable,
}

/// <summary>
/// A biblioteca aberta durante a sessao do Revit: banco, indice em memoria, dados do usuario e o servidor de busca por
/// IA. Abrir leva um instante (carrega os metadados); o servidor de IA sobe em segundo plano e so quando necessario.
/// </summary>
public sealed class LibraryService : IDisposable
{
    private static bool _sqliteReady;

    public static LibraryService? Current { get; private set; }

    private static ServerSession? _session;

    /// <summary>A sessao do servidor (login, downloads, catalogo): uma por processo do Revit.</summary>
    public static ServerSession GetSession() => _session ??= new ServerSession();

    public ServerSession Session { get; }

    public FamiliasSettings Settings { get; }
    public LibraryDb Db { get; }
    public LibraryIndex Index { get; }
    public UserStore User { get; } = new();

    public SemanticClient? Semantic { get; private set; }
    public SemanticState SemanticState { get; private set; }
    public string SemanticDetail { get; private set; } = "";
    private Task? _starting;

    private LibraryService(FamiliasSettings settings, LibraryDb db, LibraryIndex index, ServerSession session)
    {
        Settings = settings;
        Session = session;
        Db = db;
        Index = index;
        SemanticState = SemanticState.Off;
    }

    /// <summary>O provedor do SQLite precisa ser iniciado uma vez por processo.</summary>
    public static void InitSqlite()
    {
        if (_sqliteReady) return;
        SQLitePCL.Batteries_V2.Init();
        _sqliteReady = true;
    }

    /// <summary>Abre (ou reabre, se as configuracoes mudaram) a biblioteca. Lanca com mensagem clara se faltar algo.</summary>
    public static LibraryService Open(FamiliasSettings settings)
    {
        InitSqlite();
        var problems = settings.Problems();
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));

        Current?.Dispose();
        Current = null;
        var session = GetSession();
        if (settings.CatalogSync) ServerSession.ApplyPending(settings.EffectiveDb); // catalogo novo baixado numa sessao anterior
        if (!File.Exists(settings.EffectiveDb))
            throw new CatalogMissingException(settings.CatalogSync
                ? "O catalogo da biblioteca ainda nao foi baixado do servidor."
                : $"O catalogo da biblioteca nao esta instalado neste computador ({settings.EffectiveDb}).\n\nRode de novo o instalador Gestao BIM: o catalogo vai junto com ele.");
        session.Downloaded.Refresh(settings.EffectiveRoot);
        var db = LibraryDb.Open(settings.EffectiveDb);
        try
        {
            Current = new LibraryService(settings, db, db.LoadIndex(), session);
        }
        catch
        {
            db.Dispose();
            throw;
        }
        return Current;
    }

    public static void Close()
    {
        Current?.Dispose();
        Current = null;
    }

    public string? FamilyPath(FamilyRecord record) => FamiliasPaths.ResolveFamilyPath(Settings.EffectiveRoot, record.RelPath);

    /// <summary>Ja esta na pasta das familias baixadas (no caminho do catalogo ou, pelo nome e tamanho, em outra subpasta)?</summary>
    public bool IsAvailable(FamilyRecord record) => Session.Downloaded.IsDownloaded(record);

    private string LedgerFile => DownloadLedger.FileFor(FamiliasPaths.LocalHome, Settings.EffectiveRoot);

    /// <summary>
    /// Garante o arquivo da familia: usa o que ja esta na pasta se o SHA-256 conferir (inclusive uma familia que o usuario
    /// ja tinha, em outra subpasta); senao baixa do servidor (um de cada vez), confere o hash e limpa o cache se passar do
    /// limite. Devolve o caminho pronto para o Revit carregar.
    /// </summary>
    public async Task<string> EnsureFamilyFileAsync(FamilyRecord record, IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        var root = Settings.EffectiveRoot;
        if (Session.Downloaded.FindRelative(record) is { } existing
            && FamiliasPaths.ResolveFamilyPath(root, existing) is { } existingPath
            && !string.Equals(existingPath, FamilyPath(record), StringComparison.OrdinalIgnoreCase))
        {
            // Mesmo nome e tamanho em outra subpasta: so serve se for o mesmo arquivo (SHA-256 do catalogo).
            if (await Task.Run(() => DownloadManager.IsValidLocal(existingPath, record.Sha), cancel).ConfigureAwait(false)) return existingPath;
            Session.Downloaded.Forget(existing);
        }

        var result = await Session.Downloads.EnsureAsync(record, root, progress, cancel).ConfigureAwait(false);
        Session.Downloaded.MarkDownloaded(record, new FileInfo(result.Path).Length);
        if (result.Status != DownloadStatus.Downloaded) return result.Path;

        var ledger = new DownloadLedger(LedgerFile);
        ledger.Add(record.RelPath);
        if (Settings.CacheLimitGb > 0)
        {
            var limit = Settings.CacheLimitGb * 1024L * 1024 * 1024;
            var keep = result.Path;
            var session = Session;
            _ = Task.Run(() =>
            {
                try
                {
                    // So apaga o que o plugin baixou: as familias que o usuario ja tinha na pasta ficam.
                    if (CacheEvictor.Evict(root, limit, [keep], deletable: ledger.Load()) > 0) session.Downloaded.Refresh(root);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            });
        }
        return result.Path;
    }

    public string? GbfamExe => Settings.ResolveGbfam(Path.GetDirectoryName(typeof(LibraryService).Assembly.Location));

    /// <summary>Sobe o servidor de IA se ainda nao subiu. Seguro chamar varias vezes. Nunca lanca: o estado diz o que houve.</summary>
    public Task EnsureSemanticAsync()
    {
        if (SemanticState is SemanticState.Ready or SemanticState.Unavailable) return Task.CompletedTask;
        return _starting ??= StartSemantic();
    }

    private async Task StartSemantic()
    {
        SemanticState = SemanticState.Starting;
        var exe = GbfamExe;
        if (exe is null)
        {
            SemanticState = SemanticState.Unavailable;
            SemanticDetail = "Motor da busca por IA nao encontrado. Rode o instalador Gestao BIM de novo.";
            return;
        }
        try
        {
            Semantic = await SemanticClient.StartAsync(exe, Settings.EffectiveDb, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            SemanticState = SemanticState.Ready;
            SemanticDetail = "Busca por IA pronta.";
        }
        catch (Exception error)
        {
            SemanticState = SemanticState.Unavailable;
            SemanticDetail = $"Busca por IA indisponivel: {error.Message}";
        }
    }

    public void Dispose()
    {
        Semantic?.Dispose();
        Semantic = null;
        Db.Dispose();
    }
}
