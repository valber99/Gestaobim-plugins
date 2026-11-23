using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.Ai.Revit;
using Gestaobim.Render.UI;

namespace Gestaobim.Render.Revit;

public static class RenderFlow
{
    public const string Title = "GB Render";

    public static void Run(UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument ?? throw new InvalidOperationException("Abra um projeto antes de gerar o render.");
        var doc = uiDoc.Document;
        if (doc.IsFamilyDocument) throw new InvalidOperationException("O GB Render trabalha em projetos, não em famílias.");
        if (uiDoc.ActiveView is not View3D view)
            throw new InvalidOperationException("Abra a vista 3D que será renderizada (em perspectiva para um enquadramento de câmera, ou ortográfica).");
        var scene = SceneExtractor.Extract(doc, view);
        RenderWindow.Show(scene, doc.Title);
    }
}
