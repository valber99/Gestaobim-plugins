using System;
using System.Collections.Generic;
using System.Linq;

namespace Gestaobim.Cotas.Core;

/// <summary>Contagens do relatorio (<c>autodim_audit.json</c>).</summary>
public sealed record SolverAudit(int Chains, int Placed, int Unplaced, int Segments, int ShiftedTexts, int ChainCollisions, int ObstacleCollisions);

/// <summary>
/// Posiciona as cadeias do plano: converte milimetros de papel em milimetros do modelo pela escala da vista, empilha as linhas
/// (interna, intermediaria, externa) a partir da fachada e empurra para fora toda linha cuja faixa bata em outra cadeia, numa tag,
/// num texto ou numa cota que ja existe. Se nao couber dentro do limite, a cadeia vai para <see cref="SolverResult.Unplaced"/>
/// com o motivo, e as outras seguem.
/// </summary>
public static class DimSolver
{
    private const double LineHalfPaperMm = 0.3;
    private const double ExtensionPaperMm = 1.0;

    public static SolverResult Solve(IReadOnlyList<Chain> chains, ViewSummary view, RuleProfile rules)
    {
        rules.Validate();
        if (view.Scale <= 0) throw new ArgumentException("A escala da vista deve ser maior que zero.", nameof(view));
        var scale = view.Scale;
        var margin = rules.CollisionMarginPaperMm * scale;
        var step = rules.LineSpacingPaperMm * scale / 2;

        var placed = new List<PlacedChain>();
        var unplaced = new List<UnplacedChain>();

        foreach (var group in chains.GroupBy(c => (c.Dir, c.Side)).OrderBy(g => g.Key.Side))
        {
            var (edge, sign) = Edge(group.Key.Side, view.Building);
            var slot = 0;
            foreach (var chain in group.OrderBy(c => c.Level))
            {
                var refs = new List<RefPoint>();
                string? problem = null;
                foreach (var id in chain.RefIds)
                {
                    var point = view.Find(id);
                    if (point is null) { problem = $"A referencia \"{id}\" nao existe nesta vista."; break; }
                    if (point.Dir != chain.Dir) { problem = $"A referencia \"{id}\" mede em outra direcao."; break; }
                    refs.Add(point);
                }
                if (problem is null && refs.Count < 2) problem = "Menos de 2 referencias.";
                if (problem is not null) { unplaced.Add(new UnplacedChain(chain, problem)); continue; }

                refs.Sort((a, b) => a.Pos.CompareTo(b.Pos));
                var lengths = new List<double>();
                for (var i = 1; i < refs.Count; i++) lengths.Add(refs[i].Pos - refs[i - 1].Pos);
                if (lengths.Any(l => l < PlanValidator.MinSpacingMm))
                {
                    unplaced.Add(new UnplacedChain(chain, "Duas referencias na mesma posicao."));
                    continue;
                }
                var shifted = new List<int>();
                for (var i = 0; i < lengths.Count; i++)
                    if (lengths[i] / scale < rules.TextWidthPaperMm(lengths[i]) + 0.5) shifted.Add(i);

                var textRows = shifted.Count > 0 ? 2 : 1;
                var textZone = (rules.TextHeightPaperMm * textRows + rules.TextGapPaperMm) * scale;
                var ext = ExtensionPaperMm * scale;
                var lineHalf = LineHalfPaperMm * scale;
                var first = refs[0].Pos;
                var last = refs[^1].Pos;

                var baseOffset = (rules.FirstOffsetPaperMm + slot * rules.LineSpacingPaperMm) * scale;
                PlacedChain? result = null;
                var attempts = 0;
                for (var push = 0; push <= rules.MaxPushSteps; push++)
                {
                    attempts++;
                    var line = edge + sign * (baseOffset + push * step);
                    var strip = chain.Dir == Dir.X
                        ? new Box(first - ext, line - lineHalf, last + ext, line + textZone)
                        : new Box(line - textZone, first - ext, line + lineHalf, last + ext);
                    if (view.Obstacles.Any(o => strip.Intersects(o, margin))) continue;
                    if (placed.Any(p => strip.Intersects(p.Strip, margin))) continue;
                    result = new PlacedChain(chain, refs, line, strip, shifted, lengths, attempts);
                    break;
                }
                if (result is null)
                {
                    unplaced.Add(new UnplacedChain(chain, $"Nao coube: {rules.MaxPushSteps} tentativas de afastar a linha bateram em tags, textos ou outras cotas."));
                    continue;
                }
                placed.Add(result);
                slot++;
            }
        }
        return new SolverResult(placed, unplaced);
    }

    /// <summary>Conta de novo as colisoes do resultado (e o que o teste de aceite exige: zero).</summary>
    public static SolverAudit Audit(SolverResult result, ViewSummary view, RuleProfile rules)
    {
        var margin = rules.CollisionMarginPaperMm * view.Scale;
        var chainCollisions = 0;
        for (var i = 0; i < result.Placed.Count; i++)
            for (var j = i + 1; j < result.Placed.Count; j++)
                if (result.Placed[i].Strip.Intersects(result.Placed[j].Strip, margin)) chainCollisions++;
        var obstacleCollisions = result.Placed.Sum(p => view.Obstacles.Count(o => p.Strip.Intersects(o, margin)));
        return new SolverAudit(
            result.Placed.Count + result.Unplaced.Count,
            result.Placed.Count,
            result.Unplaced.Count,
            result.Placed.Sum(p => p.SegmentLengths.Count),
            result.Placed.Sum(p => p.ShiftedSegments.Count),
            chainCollisions,
            obstacleCollisions);
    }

    /// <summary>Coordenada da fachada deste lado e o sentido "para fora".</summary>
    public static (double Edge, int Sign) Edge(Side side, Box building) => side switch
    {
        Side.Top => (building.MaxY, 1),
        Side.Bottom => (building.MinY, -1),
        Side.Left => (building.MinX, -1),
        Side.Right => (building.MaxX, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    };
}
