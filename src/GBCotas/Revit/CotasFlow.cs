using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Gestaobim.Ai.Revit;
using Gestaobim.Cotas.Core;
using Gestaobim.Cotas.UI;

namespace Gestaobim.Cotas.Revit;

/// <summary>Pastas e o perfil de regras do usuario (JSON editavel, um por escritorio).</summary>
public static class CotasPaths
{
    public static string Home
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("GB_COTAS_HOME");
            return !string.IsNullOrEmpty(overrideDir)
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestaoBIM", "Cotas");
        }
    }

    public static string ProfileFile => Path.Combine(Home, "perfil-regras.json");
    public static string AuditFile => Path.Combine(Home, "autodim_audit.json");

    /// <summary>Le o perfil; se ainda nao existir, grava o padrao (NBR 6492) para o usuario editar.</summary>
    public static RuleProfile LoadProfile()
    {
        Directory.CreateDirectory(Home);
        if (!File.Exists(ProfileFile))
        {
            File.WriteAllText(ProfileFile, new RuleProfile().ToJson(), new UTF8Encoding(false));
            return new RuleProfile();
        }
        try
        {
            return RuleProfile.FromJson(File.ReadAllText(ProfileFile, Encoding.UTF8));
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new InvalidOperationException($"O perfil de regras tem um erro: {error.Message}\nArquivo: {ProfileFile}\nCorrija o arquivo ou apague-o para voltar ao padrão.");
        }
    }
}

/// <summary>O relatorio de uma cotagem (autodim_audit.json): contagens, colisoes, desvios e o que foi recusado.</summary>
public static class AuditWriter
{
    public static JsonObject Build(string viewName, double scale, PlanOutcome plan, SolverResult solved, SolverAudit solverAudit, ApplyReport? applied, IReadOnlyList<string> extractionWarnings)
    {
        var json = new JsonObject
        {
            ["view"] = viewName,
            ["scale"] = scale,
            ["plan_source"] = plan.Source.ToString(),
            ["plan_retried"] = plan.Retried,
            ["plan_note"] = plan.Note,
            ["chains_planned"] = plan.Chains.Count,
            ["chains_rejected_by_validator"] = new JsonArray(plan.Rejected.Select(e => (JsonNode?)new JsonObject { ["chain"] = e.ChainId, ["reason"] = e.Message }).ToArray()),
            ["chains_placed"] = solverAudit.Placed,
            ["chains_unplaced"] = new JsonArray(solved.Unplaced.Select(u => (JsonNode?)new JsonObject { ["chain"] = u.Chain.Id, ["reason"] = u.Reason }).ToArray()),
            ["dimension_strings_checked"] = solverAudit.Chains,
            ["aabb_collisions_between_chains"] = solverAudit.ChainCollisions,
            ["aabb_collisions_with_obstacles"] = solverAudit.ObstacleCollisions,
            ["segments"] = solverAudit.Segments,
            ["text_shifted"] = solverAudit.ShiftedTexts,
            ["extraction_warnings"] = new JsonArray(extractionWarnings.Select(w => (JsonNode?)w).ToArray()),
        };
        if (applied is not null)
        {
            json["native_dimensions_created"] = applied.Created.Count;
            json["native_dimensions"] = new JsonArray(applied.Created.Select(c => (JsonNode?)new JsonObject
            {
                ["chain"] = c.ChainId,
                ["element_id"] = c.DimensionId,
                ["segments"] = c.Segments,
                ["max_deviation_mm"] = double.IsInfinity(c.MaxDeviationMm) ? null : Math.Round(c.MaxDeviationMm, 2),
                ["shifted_texts"] = c.ShiftedTexts,
            }).ToArray());
            json["apply_failed"] = new JsonArray(applied.Failed.Select(f => (JsonNode?)new JsonObject { ["chain"] = f.ChainId, ["reason"] = f.Reason }).ToArray());
            json["apply_warnings"] = new JsonArray(applied.Warnings.Select(w => (JsonNode?)w).ToArray());
            json["revit_warnings_suppressed"] = applied.RevitWarningsSuppressed;
        }
        return json;
    }

    public static void Save(JsonObject json, string? path = null)
    {
        path ??= CotasPaths.AuditFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}

public static class CotasFlow
{
    public const string Title = "GB Cotas";

    public static void Run(UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument ?? throw new InvalidOperationException("Abra um projeto antes de cotar.");
        var doc = uiDoc.Document;
        var view = uiDoc.ActiveView;
        if (doc.IsFamilyDocument) throw new InvalidOperationException("O GB Cotas trabalha em projetos, não em famílias.");
        var rules = CotasPaths.LoadProfile();
        var extraction = ViewExtractor.Extract(doc, view, uiDoc.Selection.GetElementIds());

        var chosen = CotasWindow.Show(extraction, rules, view.Name);
        if (chosen is null) return;

        var activeRules = chosen.Rules;
        var applied = DimensionWriter.Apply(doc, view, extraction, chosen.Selected, activeRules);
        var solverAudit = DimSolver.Audit(new SolverResult(chosen.Selected, chosen.Result.Unplaced), extraction.Summary, activeRules);
        var audit = AuditWriter.Build(view.Name, extraction.Summary.Scale, chosen.Plan, chosen.Result, solverAudit, applied, extraction.Warnings);
        AuditWriter.Save(audit);

        var message = new StringBuilder();
        message.AppendLine($"{applied.Created.Count} cadeia(s) de cotas criada(s) na vista \"{view.Name}\".");
        if (applied.Failed.Count > 0)
        {
            message.AppendLine($"\n{applied.Failed.Count} não foi(ram) criada(s):");
            foreach (var f in applied.Failed) message.AppendLine($"  • {f.ChainId}: {f.Reason}");
        }
        foreach (var w in applied.Warnings) message.AppendLine("\n" + w);
        message.AppendLine("\nPara desfazer, use Ctrl+Z (uma única operação). Relatório: " + CotasPaths.AuditFile);
        Dialogs.Alert(message.ToString(), Title);
    }

    public static void OpenProfile()
    {
        CotasPaths.LoadProfile();
        Process.Start(new ProcessStartInfo(CotasPaths.ProfileFile) { UseShellExecute = true });
    }
}
