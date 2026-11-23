using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gestaobim.Cotas.Core;

public sealed record PlanParseResult(IReadOnlyList<Chain> Chains, IReadOnlyList<ChainError> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// O plano que a IA devolve e so INTENCAO: quais referencias, em que ordem, de que lado. Este validador e a fronteira: tudo que passa
/// daqui refere-se a ids que existem, na direcao certa, e e geometricamente possivel. O que a IA escrever fora do esquema (ou um texto
/// de familia que tente instrui-la) nao muda nada alem de quais cadeias entram no preview.
/// </summary>
public static class PlanValidator
{
    public const double MinSpacingMm = 1.0;

    public static PlanParseResult Parse(string json, ViewSummary view, RuleProfile rules)
    {
        var chains = new List<Chain>();
        var errors = new List<ChainError>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true });
        }
        catch (JsonException error)
        {
            return new PlanParseResult([], [new ChainError("(plano)", "JSON invalido: " + error.Message)]);
        }
        if (root?["chains"] is not JsonArray array)
            return new PlanParseResult([], [new ChainError("(plano)", "O plano precisa de uma lista \"chains\".")]);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenSlots = new HashSet<(Dir, Side, Level)>();
        var index = 0;
        foreach (var item in array)
        {
            index++;
            var id = (item as JsonObject)?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) id = $"c{index}";
            var label = id!;
            if (chains.Count >= rules.MaxChains)
            {
                errors.Add(new ChainError(label, $"Acima do limite de {rules.MaxChains} cadeias."));
                continue;
            }
            if (item is not JsonObject obj)
            {
                errors.Add(new ChainError(label, "Cadeia mal formada."));
                continue;
            }
            if (!seenIds.Add(label))
            {
                errors.Add(new ChainError(label, "Id de cadeia repetido."));
                continue;
            }
            if (!TryEnum<Dir>(obj["dir"], out var dir)) { errors.Add(new ChainError(label, "\"dir\" deve ser x ou y.")); continue; }
            if (!TryEnum<Side>(obj["side"], out var side)) { errors.Add(new ChainError(label, "\"side\" deve ser top, bottom, left ou right.")); continue; }
            if (!TryEnum<Level>(obj["level"], out var level)) { errors.Add(new ChainError(label, "\"level\" deve ser internal, intermediate ou external.")); continue; }
            var sideDir = side is Side.Top or Side.Bottom ? Dir.X : Dir.Y;
            if (sideDir != dir) { errors.Add(new ChainError(label, $"Uma cadeia em {dir} nao pode ficar no lado {side}.")); continue; }
            if (!seenSlots.Add((dir, side, level))) { errors.Add(new ChainError(label, $"Ja existe uma cadeia {level} em {side}.")); continue; }

            if (obj["refs"] is not JsonArray refArray) { errors.Add(new ChainError(label, "Faltou a lista \"refs\".")); continue; }
            var ids = refArray.Select(r => r?.ToString() ?? "").ToList();
            var problem = CheckRefs(ids, dir, view);
            if (problem is not null) { errors.Add(new ChainError(label, problem)); continue; }
            chains.Add(new Chain(label, dir, side, level, ids));
        }
        return new PlanParseResult(chains, errors);
    }

    private static string? CheckRefs(IReadOnlyList<string> ids, Dir dir, ViewSummary view)
    {
        if (ids.Count < 2) return "Uma cadeia precisa de pelo menos 2 referencias.";
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) return "Referencia repetida na cadeia.";
        var refs = new List<RefPoint>();
        foreach (var id in ids)
        {
            var point = view.Find(id);
            if (point is null) return $"A referencia \"{id}\" nao existe nesta vista.";
            if (point.Dir != dir) return $"A referencia \"{id}\" mede em {point.Dir}, nao em {dir}.";
            refs.Add(point);
        }
        var sorted = refs.OrderBy(r => r.Pos).ToList();
        for (var i = 1; i < sorted.Count; i++)
            if (sorted[i].Pos - sorted[i - 1].Pos < MinSpacingMm)
                return $"As referencias \"{sorted[i - 1].Id}\" e \"{sorted[i].Id}\" estao na mesma posicao (cota de comprimento zero).";
        return null;
    }

    private static bool TryEnum<T>(JsonNode? node, out T value) where T : struct, Enum
    {
        value = default;
        var text = node?.ToString();
        return !string.IsNullOrWhiteSpace(text) && Enum.TryParse(text.Trim(), ignoreCase: true, out value) && Enum.IsDefined(value)
               && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
