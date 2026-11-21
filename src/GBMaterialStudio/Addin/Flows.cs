using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.MaterialStudio.Revit;
using Gestaobim.MaterialStudio.UI;

namespace Gestaobim.MaterialStudio.Addin;

/// <summary>What each ribbon button does.</summary>
public static class Flows
{
    private const string EditTitle = "Gestão BIM | Editar material";
    private const string ApplyTitle = "Gestão BIM | Aplicar material em tipo";

    /// <summary>Pick a face, then edit its material (modal, or in a window that stays beside Revit).</summary>
    public static void EditMaterial(UIApplication app, bool modeless)
    {
        var title = modeless ? "Gestão BIM | Editor fixo" : EditTitle;
        var uidoc = app.ActiveUIDocument;
        if (uidoc is null)
        {
            Dialogs.Alert("Abra um documento e selecione uma face com material.", title);
            return;
        }
        var doc = uidoc.Document;
        Reference reference;
        try
        {
            reference = FacePicker.Pick(uidoc, new MaterialFaceFilter(doc), "Selecione uma face para editar o material");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            Dialogs.Alert($"Nao foi possivel selecionar a face.\n\n{error.Message}", title);
            return;
        }

        var material = MaterialServices.GetMaterialFromFace(doc, reference);
        if (material is null)
        {
            Dialogs.Alert("A face selecionada nao possui um material editavel.", title);
            return;
        }
        var request = MaterialEditorWindow.Show(uidoc, material, modeless);
        if (!modeless && request.OpenNative)
            MaterialEditorWindow.OpenNativeMaterialBrowser(uidoc);
    }

    public static void OpenFavorites(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        if (uidoc is null || uidoc.Document.IsFamilyDocument)
        {
            Dialogs.Alert("Abra um projeto para acessar e carregar seus materiais favoritos.");
            return;
        }
        FavoritesWindow.Run(uidoc);
    }

    public static void Organize(UIApplication app)
    {
        var doc = app.ActiveUIDocument?.Document;
        if (doc is null || doc.IsFamilyDocument)
        {
            Dialogs.Alert("Abra um projeto para organizar seus materiais.");
            return;
        }
        OrganizeWindow.Show(doc);
    }

    /// <summary>Change the material of chosen layers of wall, floor, roof or ceiling types.</summary>
    public static void ApplyToType(UIApplication app)
    {
        var uidoc = app.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (uidoc is null || doc is null || doc.IsFamilyDocument)
        {
            Dialogs.Alert("Abra um projeto para aplicar materiais em tipos.", ApplyTitle);
            return;
        }

        var selected = uidoc.Selection.GetElementIds();
        var types = selected.Count > 0 ? TypeLayers.TypesOfElements(doc, selected) : [];
        if (types.Count == 0)
        {
            var available = Unique(TypeLayers.AllCompoundTypes(doc), t => $"{t.Category?.Name ?? ""} : {t.Name}");
            if (available.Count == 0)
            {
                Dialogs.Alert("O projeto nao tem tipos de parede, piso, cobertura ou forro.", ApplyTitle);
                return;
            }
            var names = Dialogs.SelectFromList(Sorted(available.Keys), ApplyTitle, multiple: true, "Escolher camadas");
            if (names.Count == 0) return;
            types = names.Select(n => available[n]).ToList();
        }

        var layers = Unique(TypeLayers.Layers(doc, types), l => l.Display);
        var chosenLayers = Dialogs.SelectFromList(Sorted(layers.Keys), "Camadas que receberao o material", multiple: true, "Escolher material");
        if (chosenLayers.Count == 0) return;
        var rows = chosenLayers.Select(n => layers[n]).ToList();

        var choices = new Dictionary<string, (Material? Material, FavoriteItem? Favorite)>();
        foreach (var material in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
            choices[material.Name] = (material, null);
        try
        {
            foreach (var favorite in Favorites.List())
                choices[$"★ {favorite.Name}  (favorito)"] = (null, favorite);
        }
        catch (Exception)
        {
            // Without favorites, the project's own materials are still offered.
        }
        var picked = Dialogs.SelectFromList(Sorted(choices.Keys), "Material de destino", multiple: false, "Aplicar");
        if (picked.Count == 0) return;

        var (chosenMaterial, chosenFavorite) = choices[picked[0]];
        Material target;
        if (chosenFavorite is not null)
        {
            try
            {
                target = Favorites.GetOrCreateMaterial(app.Application, doc, chosenFavorite.Data).Material;
            }
            catch (Exception error)
            {
                Dialogs.Alert($"Nao foi possivel carregar o favorito.\n\n{error.Message}", ApplyTitle);
                return;
            }
        }
        else
        {
            target = chosenMaterial!;
        }

        LayerAssignResult result;
        try
        {
            result = TypeLayers.Assign(doc, rows, target.Id);
        }
        catch (Exception error)
        {
            Dialogs.Alert($"Nada foi alterado.\n\n{error.Message}", ApplyTitle);
            return;
        }
        var message = $"{result.Changed} camada(s) agora usam {target.Name}.";
        if (result.Failures.Count > 0)
            message += "\n\nNao alterados:\n" + string.Join("\n", result.Failures);
        Dialogs.Alert(message, ApplyTitle);
    }

    private static List<string> Sorted(IEnumerable<string> names) =>
        names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Keys items by their text; a repeated text gets a counter so no item is lost.</summary>
    private static Dictionary<string, T> Unique<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var result = new Dictionary<string, T>();
        foreach (var item in items)
        {
            var name = key(item);
            var candidate = name;
            for (var n = 2; result.ContainsKey(candidate); n++)
                candidate = $"{name} ({n})";
            result[candidate] = item;
        }
        return result;
    }
}
