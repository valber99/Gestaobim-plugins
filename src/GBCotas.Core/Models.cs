using System;
using System.Collections.Generic;
using System.Linq;

namespace Gestaobim.Cotas.Core;

/// <summary>Direcao medida pela cota, nos eixos da VISTA (x = para a direita, y = para cima), valha a vista que valer.</summary>
public enum Dir { X, Y }

/// <summary>De que lado da construcao a linha de cota fica. Cotas em X ficam em cima ou embaixo; em Y, a esquerda ou a direita.</summary>
public enum Side { Top, Bottom, Left, Right }

/// <summary>Nivel da cadeia: interna (vaos), intermediaria (eixos e paredes) e externa (medida total). A externa fica mais longe.</summary>
public enum Level { Internal = 0, Intermediate = 1, External = 2 }

/// <summary>O que a referencia e no modelo.</summary>
public enum RefKind { ExteriorFace, InteriorFace, WallCore, Axis, Opening, Column }

/// <summary>
/// Uma referencia de cota ja extraida do Revit: um plano perpendicular a direcao medida.
/// <paramref name="Pos"/> e a coordenada ao longo da direcao medida (mm, nos eixos da vista); <paramref name="PerpMin"/> e
/// <paramref name="PerpMax"/> sao o trecho que a referencia ocupa na outra direcao (uma face de parede ocupa o comprimento da parede),
/// usado para saber a que fachada ela pertence. O <paramref name="Id"/> e o que a IA ve (r01, r02...): ela nunca recebe
/// coordenadas para alterar.
/// </summary>
public sealed record RefPoint(string Id, Dir Dir, RefKind Kind, double Pos, double PerpMin, double PerpMax, string Label, long? ElementId = null);

/// <summary>Retangulo alinhado aos eixos da vista, em mm. Usado para tags, textos, cotas existentes e as faixas das cadeias.</summary>
public readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
{
    public bool Intersects(Box other, double margin = 0) =>
        MinX < other.MaxX + margin && MaxX > other.MinX - margin && MinY < other.MaxY + margin && MaxY > other.MinY - margin;

    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
}

/// <summary>O resumo da vista que o planejador (por regras ou por IA) recebe.</summary>
public sealed record ViewSummary(
    double Scale,
    string ViewName,
    string Discipline,
    Box Building,
    IReadOnlyList<RefPoint> Refs,
    IReadOnlyList<Box> Obstacles)
{
    public IEnumerable<RefPoint> RefsOf(Dir dir) => Refs.Where(r => r.Dir == dir);

    public RefPoint? Find(string id) => Refs.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
}

/// <summary>Uma cadeia do plano: quais referencias, em que direcao, de que lado e em que nivel.</summary>
public sealed record Chain(string Id, Dir Dir, Side Side, Level Level, IReadOnlyList<string> RefIds);

public sealed record ChainError(string ChainId, string Message);

/// <summary>Cadeia ja posicionada: a linha de cota esta em <see cref="LinePos"/> (mm, na direcao perpendicular a medida).</summary>
public sealed record PlacedChain(
    Chain Chain,
    IReadOnlyList<RefPoint> Refs,          // em ordem crescente de Pos
    double LinePos,
    Box Strip,                              // faixa ocupada: linha, texto e eventuais textos deslocados
    IReadOnlyList<int> ShiftedSegments,     // segmentos curtos demais para o texto: o texto sobe para a segunda fileira
    IReadOnlyList<double> SegmentLengths,
    int Attempts);

/// <summary>Cadeia que nao coube: o motivo vai para o preview e para o relatorio.</summary>
public sealed record UnplacedChain(Chain Chain, string Reason);

public sealed record SolverResult(IReadOnlyList<PlacedChain> Placed, IReadOnlyList<UnplacedChain> Unplaced);
