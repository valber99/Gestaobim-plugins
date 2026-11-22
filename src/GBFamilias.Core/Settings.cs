using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Gestaobim.Familias.Core.Server;

namespace Gestaobim.Familias.Core;

public enum LibraryMode
{
    /// <summary>Banco e familias numa pasta (o pacote offline do administrador, ou uma pasta de rede).</summary>
    Local,
    /// <summary>Catalogo local sincronizado com o servidor; cada familia e baixada ao inserir.</summary>
    Server,
}

/// <summary>Configuracao do usuario: de onde vem a biblioteca, onde ficam as familias baixadas e a busca por IA.</summary>
public sealed class FamiliasSettings
{
    public LibraryMode Mode { get; set; } = LibraryMode.Server;

    // ---- modo local
    public string LibraryDb { get; set; } = Path.Combine(FamiliasPaths.DefaultLibraryDir, "gbfam.sqlite");
    public string LibraryRoot { get; set; } = Path.Combine(FamiliasPaths.DefaultLibraryDir, "familias");

    // ---- modo servidor
    /// <summary>Pasta (obrigatoria) das familias baixadas. De livre escolha: local ou de rede.</summary>
    public string DownloadsFolder { get; set; } = "";
    /// <summary>Limite do cache em GB (0 = sem limite). So vale para pasta local; pasta de rede nunca e apagada.</summary>
    public int CacheLimitGb { get; set; } = 10;
    /// <summary>
    /// Atualizar o catalogo pelo servidor (delta assinado). Desligado por padrao: o catalogo vem no INSTALADOR do plugin
    /// e o servidor so entrega os arquivos .rfa. Fica disponivel para quando o acervo crescer.
    /// </summary>
    public bool CatalogSync { get; set; }
    /// <summary>Com CatalogSync ligado, procura atualizacoes do catalogo no maximo a cada N horas.</summary>
    public int SyncEveryHours { get; set; } = 6;

    /// <summary>Vazio = procurar o gbfam.exe automaticamente.</summary>
    public string GbfamExe { get; set; } = "";
    public bool SemanticEnabled { get; set; } = true;

    private static string FilePath => Path.Combine(FamiliasPaths.Home, "settings.json");

    /// <summary>O banco que o plugin le: o catalogo sincronizado (servidor) ou o banco informado (local).</summary>
    public string EffectiveDb => Mode == LibraryMode.Server ? FamiliasPaths.CatalogDb : LibraryDb;

    /// <summary>A pasta onde ficam os .rfa: a de downloads (servidor) ou a da biblioteca (local).</summary>
    public string EffectiveRoot => Mode == LibraryMode.Server ? DownloadsFolder : LibraryRoot;

    public static FamiliasSettings Load()
    {
        var settings = new FamiliasSettings();
        if (FamiliasPaths.ReadJsonOrQuarantine(FilePath) is not { } data) return settings;
        // Instalacoes da 0.1.0 nao tem "mode": continuam no modo local, como o usuario deixou.
        settings.Mode = Read(data, "mode") is { } m
            ? (string.Equals(m, "server", StringComparison.OrdinalIgnoreCase) ? LibraryMode.Server : LibraryMode.Local)
            : LibraryMode.Local;
        if (Read(data, "library_db") is { } db) settings.LibraryDb = db;
        if (Read(data, "library_root") is { } root) settings.LibraryRoot = root;
        if (Read(data, "downloads_folder") is { } folder) settings.DownloadsFolder = folder;
        if (Int(data, "cache_limit_gb") is { } limit && limit >= 0) settings.CacheLimitGb = limit;
        if (Int(data, "sync_every_hours") is { } hours && hours > 0) settings.SyncEveryHours = hours;
        if (data["catalog_sync"] is JsonValue cs && cs.TryGetValue<bool>(out var sync)) settings.CatalogSync = sync;
        if (Read(data, "gbfam_exe") is { } exe) settings.GbfamExe = exe;
        if (data["semantic_enabled"] is JsonValue v && v.TryGetValue<bool>(out var enabled)) settings.SemanticEnabled = enabled;
        return settings;
    }

    public void Save()
    {
        var data = new JsonObject
        {
            ["mode"] = Mode == LibraryMode.Server ? "server" : "local",
            ["library_db"] = LibraryDb,
            ["library_root"] = LibraryRoot,
            ["downloads_folder"] = DownloadsFolder,
            ["cache_limit_gb"] = CacheLimitGb,
            ["sync_every_hours"] = SyncEveryHours,
            ["catalog_sync"] = CatalogSync,
            ["gbfam_exe"] = GbfamExe,
            ["semantic_enabled"] = SemanticEnabled,
        };
        FamiliasPaths.WriteAtomic(FilePath, data.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Read(JsonObject data, string key) =>
        data[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(JsonObject data, string key) =>
        data[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>O executavel da busca por IA: o configurado ou o encontrado nas pastas padrao.</summary>
    public string? ResolveGbfam(string? addinDirectory) =>
        !string.IsNullOrWhiteSpace(GbfamExe) && File.Exists(GbfamExe) ? GbfamExe : FamiliasPaths.FindGbfam(addinDirectory);

    /// <summary>O que falta para baixar familias (modo servidor): null se a pasta das familias baixadas serve.</summary>
    public string? DownloadProblem() => Mode == LibraryMode.Server ? DownloadFolder.Validate(DownloadsFolder) : null;

    /// <summary>Problemas que impedem o uso, em portugues, ou lista vazia se esta tudo certo.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Mode == LibraryMode.Server)
        {
            // A pasta das familias baixadas e obrigatoria para BAIXAR, nao para buscar: o catalogo vem no instalador,
            // entao a biblioteca abre sem ela e a pasta e pedida na primeira insercao. Se foi preenchida, tem de servir.
            if (!string.IsNullOrWhiteSpace(DownloadsFolder) && DownloadFolder.Validate(DownloadsFolder) is { } folderProblem)
                problems.Add(folderProblem);
            return problems;
        }
        if (!File.Exists(LibraryDb)) problems.Add($"O banco da biblioteca nao foi encontrado: {LibraryDb}");
        if (!Directory.Exists(LibraryRoot)) problems.Add($"A pasta das familias nao foi encontrada: {LibraryRoot}");
        return problems;
    }
}
