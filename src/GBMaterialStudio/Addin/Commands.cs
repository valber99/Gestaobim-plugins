using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.MaterialStudio.Revit;
using Gestaobim.MaterialStudio.UI;

namespace Gestaobim.MaterialStudio.Addin;

internal static class CommandRunner
{
    /// <summary>Runs a flow with the Revit session set, and reports any unexpected error instead of crashing Revit.</summary>
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
            Dialogs.Alert($"{error.Message}\n\n{error}", "Gestão BIM");
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class EditMaterialCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, app => Flows.EditMaterial(app, modeless: false));
}

[Transaction(TransactionMode.Manual)]
public sealed class EditMaterialFixedCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, app => Flows.EditMaterial(app, modeless: true));
}

[Transaction(TransactionMode.Manual)]
public sealed class FavoritesCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, Flows.OpenFavorites);
}

[Transaction(TransactionMode.Manual)]
public sealed class OrganizeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, Flows.Organize);
}

[Transaction(TransactionMode.Manual)]
public sealed class ApplyToTypeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, Flows.ApplyToType);
}
