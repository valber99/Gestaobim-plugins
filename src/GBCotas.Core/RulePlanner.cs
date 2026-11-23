using System;
using System.Collections.Generic;
using System.Linq;

namespace Gestaobim.Cotas.Core;

/// <summary>
/// Planejador sem IA: monta as tres cadeias classicas de cada fachada a partir das regras do escritorio. Serve de modo offline, de
/// reserva quando a IA falha e de referencia para medir se a IA acrescenta algo.
/// </summary>
public static class RulePlanner
{
    public static IReadOnlyList<Chain> Plan(ViewSummary view, RuleProfile rules)
    {
        var chains = new List<Chain>();
        foreach (var side in rules.FullSides.Concat(rules.ExternalOnlySides).Distinct())
        {
            var full = rules.FullSides.Contains(side);
            var dir = side is Side.Top or Side.Bottom ? Dir.X : Dir.Y;
            var all = view.RefsOf(dir).ToList();
            var facade = all.Where(r => TouchesFacade(r, side, view.Building, rules.FacadeBandMm)).ToList();

            var ends = Extremes(all, rules);
            if (ends is null) continue;
            var (first, last) = ends.Value;

            var external = new List<RefPoint> { first, last };
            AddChain(chains, side, Level.External, external);
            if (!full) continue;

            var openings = facade.Where(r => r.Kind == RefKind.Opening).ToList();
            if (openings.Count > 0)
                AddChain(chains, side, Level.Internal, Distinct(new[] { first, last }.Concat(openings)));

            var middle = facade.Where(r => r.Kind == RefKind.Axis
                                           || r.Kind == RefKind.InteriorFace
                                           || r.Kind == RefKind.Column
                                           || (rules.UseWallCore && r.Kind == RefKind.WallCore)).ToList();
            if (middle.Count > 0)
                AddChain(chains, side, Level.Intermediate, Distinct(new[] { first, last }.Concat(middle)));
        }
        return chains;
    }

    /// <summary>A referencia encosta na faixa da fachada deste lado?</summary>
    public static bool TouchesFacade(RefPoint point, Side side, Box building, double band) => side switch
    {
        Side.Top => point.PerpMax >= building.MaxY - band,
        Side.Bottom => point.PerpMin <= building.MinY + band,
        Side.Left => point.PerpMin <= building.MinX + band,
        Side.Right => point.PerpMax >= building.MaxX - band,
        _ => false,
    };

    private static (RefPoint First, RefPoint Last)? Extremes(IReadOnlyList<RefPoint> all, RuleProfile rules)
    {
        var exterior = all.Where(r => r.Kind == RefKind.ExteriorFace || (rules.UseWallCore && r.Kind == RefKind.WallCore)).ToList();
        var pool = exterior.Count >= 2 ? exterior : all.Where(r => r.Kind != RefKind.Opening).ToList();
        if (pool.Count < 2) return null;
        var first = pool.MinBy(r => r.Pos)!;
        var last = pool.MaxBy(r => r.Pos)!;
        return last.Pos - first.Pos < PlanValidator.MinSpacingMm ? null : (first, last);
    }

    /// <summary>Tira referencias na mesma posicao (uma porta encostada no canto repete a face da parede), mantendo a de maior prioridade.</summary>
    private static List<RefPoint> Distinct(IEnumerable<RefPoint> refs)
    {
        var result = new List<RefPoint>();
        foreach (var r in refs.OrderBy(r => Priority(r.Kind)).ThenBy(r => r.Id, StringComparer.Ordinal))
            if (result.All(o => Math.Abs(o.Pos - r.Pos) >= PlanValidator.MinSpacingMm)) result.Add(r);
        return result.OrderBy(r => r.Pos).ToList();
    }

    private static int Priority(RefKind kind) => kind switch
    {
        RefKind.ExteriorFace => 0,
        RefKind.WallCore => 1,
        RefKind.Axis => 2,
        RefKind.Column => 3,
        RefKind.InteriorFace => 4,
        _ => 5,
    };

    private static void AddChain(List<Chain> chains, Side side, Level level, IReadOnlyList<RefPoint> refs)
    {
        if (refs.Count < 2) return;
        var dir = side is Side.Top or Side.Bottom ? Dir.X : Dir.Y;
        chains.Add(new Chain($"{side.ToString().ToLowerInvariant()}-{level.ToString().ToLowerInvariant()}", dir, side, level, refs.Select(r => r.Id).ToList()));
    }
}
