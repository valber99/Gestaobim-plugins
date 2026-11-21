using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.DwgExport.Core;
using Gestaobim.DwgExport.Revit;
using Gestaobim.DwgExport.UI;

namespace Gestaobim.DwgExport.Addin;

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
            Dialogs.Alert($"{error.Message}\n\n{error}");
            return Result.Failed;
        }
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ExportSheetsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, app => Flows.Export(app, OutputMode.MultipleLayouts, views: false));
}

[Transaction(TransactionMode.Manual)]
public sealed class ModelSpaceCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, app => Flows.Export(app, OutputMode.ModelSpace, views: false));
}

[Transaction(TransactionMode.Manual)]
public sealed class SingleLayoutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, app => Flows.Export(app, OutputMode.SingleLayout, views: false));
}

[Transaction(TransactionMode.Manual)]
public sealed class ExportViewsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, app => Flows.Export(app, OutputMode.SeparateFiles, views: true));
}

[Transaction(TransactionMode.Manual)]
public sealed class ExportSettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, _ => Flows.SaveSettings());
}

[Transaction(TransactionMode.Manual)]
public sealed class ImportSettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, _ => Flows.LoadSettings());
}

[Transaction(TransactionMode.Manual)]
public sealed class AboutCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
        CommandRunner.Run(commandData, ref message, _ => UpdateNotifier.ShowAbout());
}
