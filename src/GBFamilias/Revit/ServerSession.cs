using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Core.Server;

namespace Gestaobim.Familias.Revit;

public enum SyncOutcome
{
    /// <summary>Nada novo (ou consultado ha pouco).</summary>
    UpToDate,
    /// <summary>Catalogo novo baixado e validado; sera usado quando a biblioteca for reaberta.</summary>
    Ready,
    /// <summary>Sem internet ou servidor fora do ar: segue com o catalogo que ja existe.</summary>
    Offline,
    NeedsLogin,
    Failed,
}

public sealed record SyncResult(SyncOutcome Outcome, string Message, PreparedCatalog? Prepared = null);

/// <summary>Login, cliente do servidor, downloads e sincronizacao do catalogo: tudo que o modo servidor precisa.</summary>
public sealed class ServerSession
{
    private static readonly HttpClient SharedHttp = CreateHttp();

    public static string PluginVersion { get; } =
        typeof(ServerSession).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public ServerConfig Config { get; }
    public AuthClient Auth { get; }
    public ServerApi Api { get; }
    public DownloadManager Downloads { get; }
    public CatalogSync Catalog { get; }
    public DownloadedIndex Downloaded { get; } = new();

    public static string PendingDir => Path.Combine(FamiliasPaths.LocalHome, "pending");
    public static string PendingFile => Path.Combine(PendingDir, "catalog.sqlite.next");
    private static string StateFile => Path.Combine(FamiliasPaths.LocalHome, "sync-state.json");

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        ServerApi.ConfigureClient(http, PluginVersion);
        return http;
    }

    public ServerSession(ServerConfig? config = null)
    {
        Config = config ?? new ServerConfig();
        Auth = new AuthClient(Config, SharedHttp, new TokenStore(FamiliasPaths.TokenFile, new DpapiProtector()));
        Api = new ServerApi(SharedHttp, Config.ApiBase, (cancel, force) => Auth.GetAccessTokenAsync(cancel, force));
        Downloads = new DownloadManager(Api, Environment.MachineName);
        Catalog = new CatalogSync(Api, Config.CatalogPublicKey, Version.Parse(PluginVersion));
    }

    /// <summary>Abre o navegador padrao na pagina de login do ZITADEL e espera o retorno.</summary>
    public Task LoginAsync(CancellationToken cancel) =>
        Auth.LoginAsync(url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }), cancel);

    // ---- catalogo

    /// <summary>Ha um catalogo novo ja validado esperando para ser instalado?</summary>
    public static bool HasPending(string localDb) =>
        File.Exists(PendingFile) && CatalogSync.LocalVersion(PendingFile) > CatalogSync.LocalVersion(localDb);

    /// <summary>Instala o catalogo pendente. Chamar so com o banco fechado. Devolve true se instalou.</summary>
    public static bool ApplyPending(string localDb)
    {
        if (!File.Exists(PendingFile)) return false;
        var pendingVersion = CatalogSync.LocalVersion(PendingFile);
        if (pendingVersion <= CatalogSync.LocalVersion(localDb))
        {
            TryDelete(PendingFile);
            return false;
        }
        CatalogSync.Commit(new PreparedCatalog(PendingFile, pendingVersion, SyncKind.Snapshot, 0), localDb);
        return true;
    }

    private DateTime LastCheck()
    {
        try
        {
            return File.Exists(StateFile) && DateTime.TryParse(File.ReadAllText(StateFile).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t.ToUniversalTime()
                : DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    private static void SaveCheck()
    {
        try
        {
            FamiliasPaths.WriteAtomic(StateFile, DateTime.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Procura um catalogo novo e o deixa pronto em <see cref="PendingDir"/>. Com <paramref name="force"/> ignora o
    /// intervalo entre consultas. Nunca lanca (o resultado diz o que houve) e nunca mexe no banco em uso.
    /// </summary>
    public async Task<SyncResult> CheckForUpdatesAsync(string localDb, int everyHours, bool force, CancellationToken cancel)
    {
        if (HasPending(localDb)) return new SyncResult(SyncOutcome.Ready, "Há um catálogo novo pronto. Ele será usado ao reabrir a biblioteca.");
        if (!force && DateTime.UtcNow - LastCheck() < TimeSpan.FromHours(Math.Max(1, everyHours)))
            return new SyncResult(SyncOutcome.UpToDate, "Catálogo verificado há pouco.");
        if (!Auth.HasSession) return new SyncResult(SyncOutcome.NeedsLogin, "Entre com a sua conta para atualizar o catálogo.");
        try
        {
            var prepared = await Catalog.PrepareAsync(localDb, PendingDir, cancel).ConfigureAwait(false);
            SaveCheck();
            if (prepared is null) return new SyncResult(SyncOutcome.UpToDate, "O catálogo já está atualizado.");
            // PrepareAsync grava catalog.sqlite.next dentro de PendingDir
            return new SyncResult(SyncOutcome.Ready, $"Catálogo {prepared.Version} baixado ({prepared.Families} famílias).", prepared);
        }
        catch (AuthRequiredException error)
        {
            return new SyncResult(SyncOutcome.NeedsLogin, error.Message);
        }
        catch (ServerUnavailableException error)
        {
            return new SyncResult(SyncOutcome.Offline, ServerMessages.Describe(error));
        }
        catch (OperationCanceledException)
        {
            return new SyncResult(SyncOutcome.Failed, "Cancelado.");
        }
        catch (Exception error) when (error is CatalogRejectedException or InvalidDataException or NotAuthorizedException or IOException
                                          or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            return new SyncResult(SyncOutcome.Failed, ServerMessages.Describe(error));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
