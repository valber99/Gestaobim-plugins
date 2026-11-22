using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.Familias.Revit;
using Gestaobim.Familias.UI;

namespace Gestaobim.Familias.Addin;

internal static class CommandRunner
{
    /// <summary>Roda um fluxo com a sessao do Revit definida e avisa de qualquer erro inesperado em vez de derrubar o Revit.</summary>
    public static Result Run(ExternalCommandData data, ref string message, Action<UIApplication> flow)
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
        catch (Exception error)
        {
            message = error.Message;
            Dialogs.Alert($"{error.Message}\n\n{error}");
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class OpenLibraryCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, _ => BrowserWindow.ShowOrActivate());
}

[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, _ =>
        {
            if (!SettingsWindow.Edit(BrowserWindow.Instance, null)) return;
            // Se a biblioteca esta aberta, reabre com as configuracoes novas.
            if (BrowserWindow.Instance is { } open)
            {
                open.Close();
                BrowserWindow.ShowOrActivate();
            }
        });
}

[Transaction(TransactionMode.Manual)]
public sealed class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, _ => Gestaobim.Shared.AboutHub.ShowAll(App.Icon));
}
