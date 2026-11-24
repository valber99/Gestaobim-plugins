using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace Gestaobim.Familias.Revit;

/// <summary>
/// A janela da biblioteca nao bloqueia o Revit (modeless), e um manipulador de botao dela roda fora de um contexto
/// da API: ali nao se pode alterar o modelo. Este despachante enfileira o trabalho num ExternalEvent, que o Revit
/// executa no momento certo.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    private static RevitDispatcher? _handler;
    private static ExternalEvent? _event;
    private readonly ConcurrentQueue<Action<UIApplication>> _queue = new();

    /// <summary>Precisa ser chamado dentro de um comando (contexto valido da API), antes do primeiro Post.</summary>
    public static void Ensure()
    {
        if (_event is not null) return;
        _handler = new RevitDispatcher();
        _event = ExternalEvent.Create(_handler);
    }

    public static bool Post(Action<UIApplication> work)
    {
        if (_handler is null || _event is null) throw new InvalidOperationException("O despachante do Revit nao foi iniciado.");
        _handler._queue.Enqueue(work);
        return _event.Raise() == ExternalEventRequest.Accepted;
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
                UI.Dialogs.Alert($"{error.Message}\n\n{error}");
            }
        }
    }

    public string GetName() => "GB Familias";
}
