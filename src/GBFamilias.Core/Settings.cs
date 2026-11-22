using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Gestaobim.Familias.Core.Server;

namespace Gestaobim.Familias.Core;

/// <summary>
/// Configuracao do usuario. Na tela so ha a pasta das familias baixadas, o limite do cache e a conta; o resto e padrao:
/// o catalogo vem do instalador, cada .rfa vem do servidor e a busca por IA usa o gbfam.exe instalado.
/// </summary>
public sealed class FamiliasSettings
{
    /// <summary>
    /// Pasta (obrigatoria para baixar) das familias. De livre escolha: local ou de rede. Pode ser uma pasta que ja tem
    /// familias (por exemplo, a biblioteca inteira): o plugin reconhece o que ja esta la e nao baixa de novo.
    /// </summary>
    public string DownloadsFolder { get; set; } = "";
    /// <summary>Limite do cache em GB (0 = sem limite, o padrao). Nunca apaga pasta de rede nem familias que nao foram baixadas pelo plugin.</summary>
    public int CacheLimitGb { get; set; }
    /// <summary>
    /// Atualizar o catalogo pelo servidor (delta assinado). Desligado por padrao: o catalogo vem no INSTALADOR e o
    /// servidor so entrega os arquivos .rfa. Fica disponivel (settings.json) para quando o acervo crescer.
    /// </summary>
    public bool CatalogSync { get; set; }
    /// <summary>Com CatalogSync ligado, procura atualizacoes do catalogo no maximo a cada N horas.</summary>
    public int SyncEveryHours { get; set; } = 6;
    /// <summary>So no settings.json (desenvolvimento e testes). Vazio = o gbfam.exe instalado.</summary>
    public string GbfamExe { get; set; } = "";

    private static string FilePath => Path.Combine(FamiliasPaths.Home, "settings.json");

    /// <summary>O catalogo que o plugin le (instalado pelo instalador).</summary>
    public string EffectiveDb => FamiliasPaths.CatalogDb;

    /// <summary>A pasta onde ficam os .rfa.</summary>
    public string EffectiveRoot => DownloadsFolder;

    public static FamiliasSettings Load()
    {
        var settings = new FamiliasSettings();
        if (FamiliasPaths.ReadJsonOrQuarantine(FilePath) is not { } data) return settings;
        if (Read(data, "downloads_folder") is { } folder) settings.DownloadsFolder = folder;
        if (Int(data, "cache_limit_gb") is { } limit && limit >= 0) settings.CacheLimitGb = limit;
        if (Int(data, "sync_every_hours") is { } hours && hours > 0) settings.SyncEveryHours = hours;
        if (data["catalog_sync"] is JsonValue cs && cs.TryGetValue<bool>(out var sync)) settings.CatalogSync = sync;
        if (Read(data, "gbfam_exe") is { } exe) settings.GbfamExe = exe;

        // Quem usava o modo local (0.1, sem "mode" ou com "mode": "local") passa ao servidor: a pasta da biblioteca vira
        // a pasta das familias baixadas, entao tudo o que ja tinha continua disponivel sem baixar de novo.
        var wasLocal = Read(data, "mode") is not { } mode || !string.Equals(mode, "server", StringComparison.OrdinalIgnoreCase);
        if (wasLocal && string.IsNullOrWhiteSpace(settings.DownloadsFolder) && Read(data, "library_root") is { } root && Directory.Exists(root))
            settings.DownloadsFolder = root;
        return settings;
    }

    public void Save()
    {
        var data = new JsonObject
        {
            ["mode"] = "server",
            ["downloads_folder"] = DownloadsFolder,
            ["cache_limit_gb"] = CacheLimitGb,
            ["sync_every_hours"] = SyncEveryHours,
            ["catalog_sync"] = CatalogSync,
        };
        if (!string.IsNullOrWhiteSpace(GbfamExe)) data["gbfam_exe"] = GbfamExe;
        FamiliasPaths.WriteAtomic(FilePath, data.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Read(JsonObject data, string key) =>
        data[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(JsonObject data, string key) =>
        data[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>O executavel da busca por IA: o do settings.json (testes) ou o instalado.</summary>
    public string? ResolveGbfam(string? addinDirectory) =>
        !string.IsNullOrWhiteSpace(GbfamExe) && File.Exists(GbfamExe) ? GbfamExe : FamiliasPaths.FindGbfam(addinDirectory);

    /// <summary>O que falta para baixar familias: null se a pasta das familias baixadas serve.</summary>
    public string? DownloadProblem() => DownloadFolder.Validate(DownloadsFolder);

    /// <summary>Problemas que impedem salvar, em portugues, ou lista vazia se esta tudo certo.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        // A pasta e obrigatoria para BAIXAR, nao para buscar: o catalogo vem no instalador, entao a biblioteca abre sem
        // ela e a pasta e pedida na primeira insercao. Se foi preenchida, tem de servir.
        if (!string.IsNullOrWhiteSpace(DownloadsFolder) && DownloadFolder.Validate(DownloadsFolder) is { } folderProblem)
            problems.Add(folderProblem);
        return problems;
    }
}
