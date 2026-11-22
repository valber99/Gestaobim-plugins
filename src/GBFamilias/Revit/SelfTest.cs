using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Core.Server;

namespace Gestaobim.Familias.Revit;

/// <summary>
/// Autoverificacao do plugin DENTRO de um Revit de verdade, sem interface: login salvo, catalogo, busca de texto e de IA,
/// download, carregar e posicionar uma familia num projeto novo (em memoria), reaproveitar, simular a falta de internet e
/// a sessao apagada. So roda quando a variavel de ambiente GB_FAMILIAS_SELFTEST aponta para o arquivo do relatorio;
/// sem ela nao faz nada. Nunca toca em um projeto do usuario (cria e descarta o proprio documento).
/// </summary>
internal static class SelfTest
{
    public const string EnvReport = "GB_FAMILIAS_SELFTEST";
    /// <summary>Arquivo JSON {access_token, expires_in} com um token de teste; e lido, gravado protegido e apagado.</summary>
    public const string EnvToken = "GB_FAMILIAS_SELFTEST_TOKEN";

    public static void Attach(UIControlledApplication ui)
    {
        if (Environment.GetEnvironmentVariable(EnvReport) is not { Length: > 0 } report) return;
        ui.ControlledApplication.ApplicationInitialized += (sender, _) =>
        {
            var log = new Reporter(report);
            try
            {
                // O login de teste e gravado ANTES de qualquer coisa criar a sessao (o "Sobre" do GB Familias ja a cria).
                if (Environment.GetEnvironmentVariable(EnvToken) is { Length: > 0 } tokenFile && File.Exists(tokenFile))
                {
                    var json = JsonNode.Parse(File.ReadAllText(tokenFile))!;
                    new TokenStore(FamiliasPaths.TokenFile, new DpapiProtector()).Save(new TokenSet(
                        json["access_token"]!.GetValue<string>(), null, DateTime.UtcNow.AddSeconds(json["expires_in"]!.GetValue<int>())));
                    File.Delete(tokenFile);
                }
                CheckAbout(ui, log);
                Run((Application)sender!, log);
            }
            catch (Exception error)
            {
                log.Check("excecao nao tratada", false, error.ToString());
            }
            finally
            {
                log.Finish();
            }
        };
    }

    private sealed class Reporter(string path)
    {
        public int Passed;
        public int Failed;

        private void Write(string line) => File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");

        public void Info(string text) => Write("INFO   " + text);

        public void Check(string name, bool ok, string detail = "")
        {
            if (ok) Passed++; else Failed++;
            Write((ok ? "PASSOU " : "FALHOU ") + name + (detail.Length > 0 ? $"  [{detail}]" : ""));
        }

        public void Finish() => Write($"FIM passou={Passed} falhou={Failed}");
    }

    private static T Sync<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    private static int? VersionYear(FamilyRecord r) =>
        int.TryParse(r.RevitVersion?.TrimStart('~'), out var year) ? year : null;

    /// <summary>O botao "Sobre" e um so para os plugins Gestao BIM instalados (ver src/GBShared/AboutHub.cs).</summary>
    private static void CheckAbout(UIControlledApplication ui, Reporter log)
    {
        const string tab = "Gestão BIM";
        var panels = ui.GetRibbonPanels(tab);
        var items = panels.SelectMany(p => p.GetItems().Select(i => (Panel: p.Name, i.ItemText))).ToList();
        log.Info("painéis da aba: " + string.Join(" | ", panels.Select(p => $"{p.Name}: {string.Join(", ", p.GetItems().Select(i => i.ItemText.Replace('\n', ' ')))}")));
        var abouts = items.Where(i => i.ItemText == "Sobre").ToList();
        log.Check("um só botão Sobre na aba Gestão BIM", abouts.Count == 1, string.Join(", ", abouts.Select(a => a.Panel)));
        log.Check("o Sobre fica no painel Geral", abouts.All(a => a.Panel == Gestaobim.Shared.AboutHub.PanelName));

        if (AppDomain.CurrentDomain.GetData("GestaoBIM.AboutHub.Providers") is not System.Collections.IDictionary providers)
        {
            log.Check("plugins registrados no Sobre", false, "nenhum");
            return;
        }
        var names = providers.Keys.Cast<string>().OrderBy(n => n).ToList();
        log.Check("plugins registrados no Sobre", names.Contains("GB Famílias"), string.Join(", ", names));
        foreach (System.Collections.DictionaryEntry entry in providers)
        {
            var section = ((Func<(string, string, string?, string?)>)entry.Value!)();
            log.Check($"Sobre: seção de {entry.Key} responde", section.Item2.Contains("Versão instalada"), section.Item2.Replace("\n", " | "));
        }
    }

    private static void Run(Application app, Reporter log)
    {
        var year = int.Parse(app.VersionNumber);
        log.Info($"{app.VersionName} build {app.VersionBuild} | plugin {ServerSession.PluginVersion} | .NET {Environment.Version}");

        var settings = FamiliasSettings.Load();
        log.Check("sem pasta de downloads a biblioteca abre, mas baixar pede a pasta",
            new FamiliasSettings { Mode = LibraryMode.Server }.Problems().Count == 0 && new FamiliasSettings { Mode = LibraryMode.Server }.DownloadProblem() is not null);
        log.Check("configuracao do modo servidor sem problemas", settings.Mode == LibraryMode.Server && settings.Problems().Count == 0 && settings.DownloadProblem() is null,
            $"{settings.Mode}; pasta {settings.DownloadsFolder}; {string.Join("|", settings.Problems())}");
        var session = LibraryService.GetSession();
        log.Check("o login salvo (DPAPI) foi encontrado", session.Auth.HasSession);

        // ---- catalogo
        LibraryService.Close();
        // O catalogo vem no instalador (o roteiro de teste o instala antes). Sem ele, o plugin precisa orientar o usuario.
        if (!File.Exists(settings.EffectiveDb))
        {
            var guided = false;
            try { LibraryService.Open(settings); }
            catch (CatalogMissingException error) { guided = error.Message.Contains("instalador", StringComparison.OrdinalIgnoreCase); }
            log.Check("sem catalogo instalado, o plugin manda rodar o instalador", guided);
            return;
        }
        log.Check("catalogo do instalador presente", CatalogSync.LocalVersion(settings.EffectiveDb) > 0, $"versao {CatalogSync.LocalVersion(settings.EffectiveDb)}");
        var lib = LibraryService.Open(settings);
        log.Check("biblioteca aberta com o catalogo do servidor", lib.Index.All.Count >= 3000, $"{lib.Index.All.Count} familias");

        // ---- busca de texto e filtros (offline por natureza)
        var spec = new FilterSpec { Text = "porta" };
        var found = lib.Index.Query(spec);
        log.Check("busca de texto 'porta' acha resultados", found.Count > 0, $"{found.Count}");
        var facets = lib.Index.CountsAll(spec);
        log.Check("contagens dos filtros calculadas", facets[Facet.Category].Count > 0 && facets.Count == Enum.GetValues<Facet>().Length);

        // ---- busca por IA (gbfam serve rodando dentro do processo do Revit)
        Sync(async () => { await lib.EnsureSemanticAsync(); return 0; });
        log.Check("busca por IA pronta", lib.SemanticState == SemanticState.Ready, $"{lib.SemanticState}: {lib.SemanticDetail}");
        if (lib.SemanticState == SemanticState.Ready)
        {
            var hits = Sync(() => lib.Semantic!.SearchAsync("lavatorio de banheiro", null, null, 10));
            log.Check("busca por IA devolve resultados", hits.Count > 0, $"{hits.Count} resultados; primeiro: {lib.Index.ById(hits[0])?.Title}");
        }

        // ---- compatibilidade de versao: familia mais nova que este Revit
        var newer = lib.Index.All.FirstOrDefault(r => VersionYear(r) is { } y && y > year);
        if (newer is null) log.Info($"nenhuma familia do catalogo foi salva em Revit mais novo que {year}");
        else
            log.Check("familia salva em Revit mais novo recebe o aviso 'nao consegue abrir'",
                FamilyCompat.Note(newer.RevitVersion, year)?.Contains("nao consegue abri", StringComparison.Ordinal) == true, $"{newer.Title} ({newer.RevitVersion})");

        // ---- baixar, carregar e posicionar num projeto novo (em memoria)
        var candidates = lib.Index.All
            .Where(r => r.HasPreview && r.SizeBytes is > 0 and < 1_500_000 && VersionYear(r) is { } y && y <= year && !lib.IsAvailable(r))
            .OrderBy(r => r.Category == "Mobiliário" ? 0 : 1).ThenBy(r => r.SizeBytes)
            .Take(15).ToList();
        log.Check("ha familias candidatas para o teste de inserir", candidates.Count > 0, $"{candidates.Count}");

        Document? doc = null;
        FamilyRecord? placed = null;
        try
        {
            var project = app.NewProjectDocument(UnitSystem.Metric);
            doc = project;
            if (project is null) { log.Check("projeto novo criado em memoria", false); return; }
            log.Check("projeto novo criado em memoria", true);
            Level level;
            using (var tx = new Transaction(project, "nivel de teste"))
            {
                tx.Start();
                level = Level.Create(project, 0);
                tx.Commit();
            }

            foreach (var record in candidates)
            {
                var path = Sync(() => lib.EnsureFamilyFileAsync(record, null, CancellationToken.None));
                log.Check($"download de '{record.Title}'", File.Exists(path) && DownloadManager.IsValidLocal(path, record.Sha),
                    $"{new FileInfo(path).Length} bytes, SHA-256 confere");

                var (family, error, reused) = FamilyInserter.LoadInto(doc!, path);
                if (family is null) { log.Info($"nao carregou '{record.Title}': {error}"); continue; }
                log.Check($"Revit carregou a familia '{family.Name}'", !reused);
                if (family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
                {
                    log.Info($"'{family.Name}' tem posicionamento {family.FamilyPlacementType}; procurando uma de piso");
                    continue;
                }
                var symbol = family.GetFamilySymbolIds().Select(id => project.GetElement(id)).OfType<FamilySymbol>().OrderBy(s => s.Name).FirstOrDefault();
                if (symbol is null) { log.Info($"'{family.Name}' sem tipos"); continue; }

                using (var tx = new Transaction(project, "posicionar"))
                {
                    tx.Start();
                    if (!symbol.IsActive) symbol.Activate();
                    project.Regenerate();
                    var instance = project.Create.NewFamilyInstance(XYZ.Zero, symbol, level, StructuralType.NonStructural);
                    tx.Commit();
                    log.Check("instancia posicionada no projeto", instance is not null && instance.Symbol.Family.Id == family.Id);
                }
                var instances = new FilteredElementCollector(project).OfClass(typeof(FamilyInstance)).GetElementCount();
                log.Check("o projeto tem exatamente 1 instancia", instances == 1, $"{instances}");

                var (again, _, reusedAgain) = FamilyInserter.LoadInto(project, path);
                log.Check("inserir de novo reaproveita a familia (nao recarrega)", reusedAgain && again?.Id == family.Id);
                placed = record;
                break;
            }
            log.Check("uma familia foi baixada, carregada e posicionada de ponta a ponta", placed is not null);
        }
        finally
        {
            try { doc?.Close(false); } catch (Exception error) { log.Info("fechar projeto de teste: " + error.Message); }
        }

        // ---- sem internet: servidor inalcancavel
        var offline = new ServerSession(new ServerConfig { ApiBase = "http://127.0.0.1:9" });
        var notDownloaded = lib.Index.All.First(r => r.SizeBytes > 0 && !lib.IsAvailable(r));
        var unavailable = false;
        try { Sync(async () => await offline.Downloads.EnsureAsync(notDownloaded, settings.EffectiveRoot, null, CancellationToken.None)); }
        catch (ServerUnavailableException) { unavailable = true; }
        log.Check("sem servidor, baixar uma familia nova diz 'sem conexao' (sem travar)", unavailable);
        if (placed is not null)
        {
            var result = Sync(() => offline.Downloads.EnsureAsync(placed, settings.EffectiveRoot, null, CancellationToken.None));
            log.Check("sem servidor, a familia ja baixada continua disponivel", result.Status == DownloadStatus.AlreadyPresent);
        }
        log.Check("sem servidor, busca e filtros seguem funcionando", lib.Index.Query(new FilterSpec { Text = "cadeira" }).Count > 0);

        // ---- sessao apagada
        session.Auth.Logout();
        var needsLogin = false;
        try { Sync(() => lib.EnsureFamilyFileAsync(notDownloaded, null, CancellationToken.None)); }
        catch (AuthRequiredException) { needsLogin = true; }
        log.Check("sem login, baixar pede o login", needsLogin);

        LibraryService.Close();
    }
}
