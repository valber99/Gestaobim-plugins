using System;
using System.IO;

namespace Gestaobim.Ai.Revit;

/// <summary>Relatorio de texto da autoverificacao dentro de um Revit de verdade (mesmo formato do GB DWG Export).</summary>
public sealed class SelfTestReporter(string path)
{
    private int _passed;
    private int _failed;

    public int Failed => _failed;

    private void Write(string line) => File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");

    public void Info(string text) => Write("INFO   " + text);

    public void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++; else _failed++;
        Write((ok ? "PASSOU " : "FALHOU ") + name + (detail.Length > 0 ? $"  [{detail}]" : ""));
    }

    public void Finish() => Write($"FIM passou={_passed} falhou={_failed}");
}

/// <summary>
/// So no autoteste: fecha sozinho os avisos que o Revit abre ao carregar projetos (por exemplo o de atualizacao de um arquivo de
/// versao antiga, que so tem "Fechar") e anota cada um no relatorio. Sem isso o teste fica parado esperando um clique.
/// </summary>
public static class SelfTestDialogs
{
    private const int Close = 8;   // TaskDialogResult.Close / IDCLOSE
    private const int Ok = 1;      // IDOK

    public static void AutoClose(Autodesk.Revit.UI.UIControlledApplication ui, SelfTestReporter log)
    {
        // So depois que o Revit terminou de iniciar: antes disso vem o aviso de complemento nao assinado dos outros plugins, que o
        // script responde ("Sempre carregar"). Responder aqui seria "Nao carregar".
        var ready = false;
        ui.ControlledApplication.ApplicationInitialized += (_, _) => ready = true;
        // Erros do proprio projeto ao abrir (elementos com problema) viram o DocWarnDialog, e responder a ele cancela a abertura. Tratados
        // antes: avisos apagados, erros com resolucao resolvidos. A copia nunca e salva.
        ui.ControlledApplication.FailuresProcessing += (_, e) =>
        {
            if (!ready) return;
            var accessor = e.GetFailuresAccessor();
            // As transacoes do proprio plugin ("GB Cotas: ...", "GB Render: ...") ficam com o tratamento delas: o teste precisa ver as falhas.
            if (accessor.GetTransactionName().StartsWith("GB ", StringComparison.Ordinal)) return;
            int warnings = 0, resolved = 0;
            foreach (var failure in accessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == Autodesk.Revit.DB.FailureSeverity.Warning) { accessor.DeleteWarning(failure); warnings++; }
                else if (failure.HasResolutions()) { accessor.ResolveFailure(failure); resolved++; }
            }
            if (warnings + resolved == 0) return;
            log.Info($"falhas do Revit tratadas no autoteste: {warnings} aviso(s) apagado(s), {resolved} erro(s) resolvido(s)");
            e.SetProcessingResult(resolved > 0 ? Autodesk.Revit.DB.FailureProcessingResult.ProceedWithCommit : Autodesk.Revit.DB.FailureProcessingResult.Continue);
        };
        ui.DialogBoxShowing += (_, e) =>
        {
            if (!ready || e.DialogId.StartsWith("TaskDialog_Security", StringComparison.Ordinal)) return;
            // TaskDialog de aviso costuma ter so "Fechar"; nos dialogos nativos (avisos ao abrir o projeto) "Fechar" e cancelar a abertura,
            // entao vai OK primeiro.
            var task = e as Autodesk.Revit.UI.Events.TaskDialogShowingEventArgs;
            var message = task?.Message ?? "";
            var closed = task is not null ? e.OverrideResult(Close) || e.OverrideResult(Ok) : e.OverrideResult(Ok) || e.OverrideResult(Close);
            log.Info($"aviso do Revit {(closed ? "fechado" : "NAO fechado")}: {e.DialogId} {message.Split('\n')[0]}");
        };
    }
}
