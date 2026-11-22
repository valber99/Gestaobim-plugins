using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Gestaobim.Familias.Revit;

public enum InsertOutcome
{
    /// <summary>A familia foi carregada e o usuario posicionou instancias (ou terminou o comando).</summary>
    Placed,
    /// <summary>Carregada, mas nao ha posicionamento automatico (documento de familia, perfil, anotacao...).</summary>
    LoadedOnly,
    /// <summary>O usuario apertou Esc antes de posicionar; a familia continua carregada.</summary>
    Cancelled,
    Failed,
}

public sealed record InsertResult(InsertOutcome Outcome, string Message);

/// <summary>Carrega uma familia no documento ativo e inicia o posicionamento. Roda sempre no contexto da API.</summary>
public static class FamilyInserter
{
    private sealed class LoadOptions : IFamilyLoadOptions
    {
        // Se a familia ja existe no projeto, preserva os valores de parametro dos tipos existentes.
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }

    /// <summary>
    /// Carrega a familia no documento (ou reaproveita a que ja existe com o mesmo nome). Roda no contexto da API.
    /// Devolve a familia, ou null com a mensagem do problema.
    /// </summary>
    public static (Family? Family, string? Error, bool Reused) LoadInto(Document doc, string path)
    {
        if (!File.Exists(path)) return (null, $"O arquivo da familia nao foi encontrado:\n{path}", false);

        var name = Path.GetFileNameWithoutExtension(path);
        var family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (family is not null) return (family, null, true);

        using var transaction = new Transaction(doc, "GB Famílias: carregar família");
        transaction.Start();
        try
        {
            if (!doc.LoadFamily(path, new LoadOptions(), out family) || family is null)
            {
                transaction.RollBack();
                return (null, "O Revit nao conseguiu carregar esta familia.", false);
            }
            transaction.Commit();
            return (family, null, false);
        }
        catch (Exception error)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            return (null, $"O Revit nao conseguiu carregar esta familia:\n{error.Message}", false);
        }
    }

    public static InsertResult Run(UIApplication app, string path)
    {
        if (!File.Exists(path)) return new(InsertOutcome.Failed, $"O arquivo da familia nao foi encontrado:\n{path}");

        var uiDoc = app.ActiveUIDocument;
        if (uiDoc is null) return new(InsertOutcome.Failed, "Abra um projeto antes de inserir uma familia.");
        var doc = uiDoc.Document;

        var (family, loadError, _) = LoadInto(doc, path);
        if (family is null) return new(InsertOutcome.Failed, loadError ?? "O Revit nao conseguiu carregar esta familia.");

        if (doc.IsFamilyDocument)
            return new(InsertOutcome.LoadedOnly, $"A familia \"{family.Name}\" foi carregada neste documento de familia.");

        var symbol = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>()
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
        if (symbol is null)
            return new(InsertOutcome.LoadedOnly, $"A familia \"{family.Name}\" foi carregada, mas nao tem tipos para posicionar.");

        if (!symbol.IsActive)
        {
            using var activate = new Transaction(doc, "GB Famílias: ativar tipo");
            activate.Start();
            symbol.Activate();
            doc.Regenerate();
            activate.Commit();
        }

        try
        {
            uiDoc.PromptForFamilyInstancePlacement(symbol);
            return new(InsertOutcome.Placed, $"Familia \"{family.Name}\" inserida.");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return new(InsertOutcome.Cancelled, $"A familia \"{family.Name}\" foi carregada. Posicionamento cancelado.");
        }
        catch (Exception error) when (error is Autodesk.Revit.Exceptions.ArgumentException or InvalidOperationException)
        {
            return new(InsertOutcome.LoadedOnly,
                $"A familia \"{family.Name}\" foi carregada, mas nao pode ser posicionada por este comando " +
                $"(por exemplo perfis e anotacoes). Procure-a no Navegador de Projeto.");
        }
    }
}
