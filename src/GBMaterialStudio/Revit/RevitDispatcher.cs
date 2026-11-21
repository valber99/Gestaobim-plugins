using Autodesk.Revit.UI;
using Gestaobim.MaterialStudio.UI;

namespace Gestaobim.MaterialStudio.Revit;

/// <summary>
/// Lets a WPF window stay open while the user keeps working in Revit. Revit only accepts API calls inside a
/// valid API context, and a modeless window's handlers run outside one, so the handlers that touch the model
/// are queued here and run the next time Revit is idle.
/// </summary>
public sealed class RevitDispatcher : IExternalEventHandler
{
    private static RevitDispatcher? _instance;
    private readonly Queue<Action> _queue = new();
    private readonly ExternalEvent _event;

    private RevitDispatcher() => _event = ExternalEvent.Create(this);

    /// <summary>The shared dispatcher. Must be first requested from a valid API context (a command).</summary>
    public static RevitDispatcher Instance => _instance ??= new RevitDispatcher();

    public static void Post(Action action) => Instance.Enqueue(action);

    private void Enqueue(Action action)
    {
        lock (_queue) _queue.Enqueue(action);
        _event.Raise();
    }

    public void Execute(UIApplication app)
    {
        RevitContext.UiApplication = app;
        List<Action> pending;
        lock (_queue)
        {
            pending = _queue.ToList();
            _queue.Clear();
        }
        foreach (var action in pending)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Dialogs.Alert($"{error.Message}\n\n{error}", "Gestão BIM");
            }
        }
    }

    public string GetName() => "GBMaterialStudio.Dispatcher";
}
