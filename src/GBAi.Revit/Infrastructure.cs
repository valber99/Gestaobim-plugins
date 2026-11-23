using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Gestaobim.Ai.Revit;

/// <summary>A sessao do Revit em execucao, preenchida por todo comando.</summary>
public static class RevitContext
{
    private static UIApplication? _ui;

    public static UIApplication UiApplication
    {
        get => _ui ?? throw new InvalidOperationException("O Revit ainda nao foi inicializado.");
        set => _ui = value;
    }

    public static IntPtr MainWindowHandle => _ui?.MainWindowHandle ?? IntPtr.Zero;
}

/// <summary>Caixas de mensagem e janelas presas ao Revit, para nunca ficarem escondidas atras dele.</summary>
public static class Dialogs
{
    private sealed class Owner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    public static bool Alert(string message, string title, bool ask = false)
    {
        var buttons = ask ? System.Windows.Forms.MessageBoxButtons.YesNo : System.Windows.Forms.MessageBoxButtons.OK;
        var icon = ask ? System.Windows.Forms.MessageBoxIcon.Question : System.Windows.Forms.MessageBoxIcon.Information;
        var handle = RevitContext.MainWindowHandle;
        var result = handle == IntPtr.Zero
            ? System.Windows.Forms.MessageBox.Show(message, title, buttons, icon)
            : System.Windows.Forms.MessageBox.Show(new Owner(handle), message, title, buttons, icon);
        return !ask || result == System.Windows.Forms.DialogResult.Yes;
    }

    public static void Attach(Window window)
    {
        try
        {
            var handle = RevitContext.MainWindowHandle;
            if (handle != IntPtr.Zero) new WindowInteropHelper(window).Owner = handle;
        }
        catch (Exception)
        {
            // Sem dono a janela funciona do mesmo jeito; so pode abrir atras do Revit.
        }
    }

    public static string? PickFolder(string? start, string description)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = System.IO.Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        var handle = RevitContext.MainWindowHandle;
        var result = handle == IntPtr.Zero ? dialog.ShowDialog() : dialog.ShowDialog(new Owner(handle));
        return result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }
}

/// <summary>
/// Janelas que nao bloqueiam o Revit rodam fora de um contexto valido da API; o que mexe no modelo vai por este despachante,
/// que enfileira o trabalho num ExternalEvent.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    private static RevitDispatcher? _handler;
    private static ExternalEvent? _event;
    private readonly ConcurrentQueue<Action<UIApplication>> _queue = new();

    /// <summary>Chamar dentro de um comando (contexto valido da API) antes do primeiro <see cref="Post"/>.</summary>
    public static void Ensure()
    {
        if (_event is not null) return;
        _handler = new RevitDispatcher();
        _event = ExternalEvent.Create(_handler);
    }

    public static void Post(Action<UIApplication> work)
    {
        if (_handler is null || _event is null) throw new InvalidOperationException("O despachante do Revit nao foi iniciado.");
        _handler._queue.Enqueue(work);
        _event.Raise();
    }

    public void Execute(UIApplication app)
    {
        while (_queue.TryDequeue(out var work))
        {
            try
            {
                work(app);
            }
            catch (Exception error)
            {
                Dialogs.Alert($"{error.Message}\n\n{error}", "Gestão BIM");
            }
        }
    }

    public string GetName() => "Gestao BIM IA";
}

/// <summary>Roda um fluxo com a sessao definida e mostra o erro inesperado em vez de derrubar o Revit.</summary>
public static class CommandRunner
{
    public static Result Run(ExternalCommandData data, ref string message, string title, Action<UIApplication> flow)
    {
        try
        {
            RevitContext.UiApplication = data.Application;
            flow(data.Application);
            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception error) when (error is InvalidOperationException or GatewayException or AiException)
        {
            // Recusas previstas ("abra uma planta", "ligue Recortar vista", gateway ausente): so a mensagem, sem a pilha de chamadas.
            message = error.Message;
            Dialogs.Alert(error.Message, title);
            return Result.Cancelled;
        }
        catch (Exception error)
        {
            message = error.Message;
            Dialogs.Alert($"{error.Message}\n\n{error}", title);
            return Result.Failed;
        }
    }
}

/// <summary>"Configurar IA": o mesmo botao para os dois plugins (o primeiro a iniciar cria o painel).</summary>
[Transaction(TransactionMode.Manual)]
public sealed class ConfigureAiCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, "Configurar IA", _ => AiSettingsWindow.ShowDialog());
}
