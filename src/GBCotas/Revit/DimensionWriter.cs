using Autodesk.Revit.DB;
using Gestaobim.Cotas.Core;

namespace Gestaobim.Cotas.Revit;

public sealed record CreatedChain(string ChainId, long DimensionId, int Segments, double MaxDeviationMm, int ShiftedTexts);

public sealed record FailedChain(string ChainId, string Reason);

public sealed class ApplyReport
{
    public List<CreatedChain> Created { get; } = [];
    public List<FailedChain> Failed { get; } = [];
    public List<string> Warnings { get; } = [];
    public int RevitWarningsSuppressed { get; set; }
}

/// <summary>
/// Cria as cotas nativas. Cada cadeia vai numa SubTransaction: se o Revit recusar uma, so ela volta atras. Depois de criar, le a cota
/// de volta e compara os valores com o plano; tudo que diverge entra no relatorio (o Revit e a fonte da verdade).
/// </summary>
public static class DimensionWriter
{
    private const double DeviationWarnMm = 2.0;
    private const double DeviationFailMm = 50.0;   // acima disso o Revit nao mediu o que o plano diz: as referencias nao servem para esta cota

    public static ApplyReport Apply(Document doc, View view, Extraction extraction, IReadOnlyList<PlacedChain> chains, RuleProfile rules)
    {
        var report = new ApplyReport();
        using var group = new TransactionGroup(doc, "GB Cotas: cotar vista");
        group.Start();
        using var transaction = new Transaction(doc, "GB Cotas: criar cotas");
        var preprocessor = new SuppressDimensionWarnings();
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(preprocessor);
        transaction.SetFailureHandlingOptions(options);
        transaction.Start();

        foreach (var placed in chains)
        {
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var refs = new ReferenceArray();
                foreach (var point in placed.Refs) refs.Append(extraction.References[point.Id]);
                var first = placed.Refs[0].Pos;
                var last = placed.Refs[^1].Pos;
                var line = placed.Chain.Dir == Dir.X
                    ? Line.CreateBound(extraction.Point(first, placed.LinePos), extraction.Point(last, placed.LinePos))
                    : Line.CreateBound(extraction.Point(placed.LinePos, first), extraction.Point(placed.LinePos, last));
                var dimension = doc.Create.NewDimension(view, line, refs);
                doc.Regenerate();

                // O Revit aceita criar uma cota entre planos que nao sao paralelos e a deixa invalida (valor -1, apagada no fim): le de volta antes.
                var deviation = dimension is { IsValidObject: true } ? Deviation(dimension, placed) : double.PositiveInfinity;
                if (double.IsInfinity(deviation) || deviation > DeviationFailMm)
                {
                    sub.RollBack();
                    report.Failed.Add(new FailedChain(placed.Chain.Id, double.IsInfinity(deviation)
                        ? "O Revit criou uma cota invalida (as referencias nao sao paralelas ou nao formam esta cadeia). A cadeia foi desfeita."
                        : $"O Revit mediu {deviation:0.#} mm de diferenca do plano. A cadeia foi desfeita."));
                    continue;
                }
                var shifted = ShiftTexts(dimension, placed, view, extraction, rules);
                if (deviation > DeviationWarnMm)
                    report.Warnings.Add($"{placed.Chain.Id}: o Revit mediu até {deviation:0.#} mm de diferença do planejado (o valor do Revit vale).");
                report.Created.Add(new CreatedChain(placed.Chain.Id, dimension.Id.Value, Math.Max(placed.SegmentLengths.Count, 1), deviation, shifted));
                sub.Commit();
            }
            catch (Exception error) when (error is Autodesk.Revit.Exceptions.ArgumentException
                                             or Autodesk.Revit.Exceptions.InvalidOperationException
                                             or KeyNotFoundException)
            {
                sub.RollBack();
                report.Failed.Add(new FailedChain(placed.Chain.Id, "O Revit recusou a cadeia: " + error.Message.Split('\n')[0].Trim()));
            }
        }
        report.RevitWarningsSuppressed = preprocessor.Suppressed;
        transaction.Commit();
        group.Assimilate();
        return report;
    }

    /// <summary>Maior diferenca, em mm, entre os valores medidos pelo Revit e os do plano.</summary>
    private static double Deviation(Dimension dimension, PlacedChain placed)
    {
        var measured = new List<double>();
        if (dimension.Segments.Size > 0)
            foreach (DimensionSegment segment in dimension.Segments) measured.Add((segment.Value ?? 0) * ViewExtractor.FtToMm);
        else measured.Add((dimension.Value ?? 0) * ViewExtractor.FtToMm);
        if (measured.Count != placed.SegmentLengths.Count) return double.PositiveInfinity;
        var worst = 0.0;
        for (var i = 0; i < measured.Count; i++) worst = Math.Max(worst, Math.Abs(measured[i] - placed.SegmentLengths[i]));
        return worst;
    }

    /// <summary>Vao curto demais para o texto: o texto sobe (ou vai para fora) uma fileira, sem mexer na cota.</summary>
    private static int ShiftTexts(Dimension dimension, PlacedChain placed, View view, Extraction extraction, RuleProfile rules)
    {
        if (placed.ShiftedSegments.Count == 0) return 0;
        var rowFt = rules.TextHeightPaperMm * view.Scale / ViewExtractor.FtToMm * 1.2;
        var direction = placed.Chain.Dir == Dir.X ? extraction.Up : extraction.Right.Negate();   // acima da linha; ou a esquerda dela
        var moved = 0;
        if (dimension.Segments.Size == 0)
        {
            try
            {
                if (dimension.IsTextPositionAdjustable())
                {
                    dimension.TextPosition += direction * rowFt;
                    return 1;
                }
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
            return 0;
        }
        foreach (var index in placed.ShiftedSegments)
        {
            if (index < 0 || index >= dimension.Segments.Size) continue;
            var segment = dimension.Segments.get_Item(index);
            try
            {
                if (segment.IsTextPositionAdjustable())
                {
                    segment.TextPosition += direction * rowFt;
                    moved++;
                }
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException) { }
        }
        return moved;
    }

    /// <summary>Avisos de cota (nao paralela, texto sobreposto) nao interrompem; erros de verdade continuam com o Revit.</summary>
    private sealed class SuppressDimensionWarnings : IFailuresPreprocessor
    {
        public int Suppressed { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var message in accessor.GetFailureMessages())
            {
                if (message.GetSeverity() != FailureSeverity.Warning) continue;
                accessor.DeleteWarning(message);
                Suppressed++;
            }
            return FailureProcessingResult.Continue;
        }
    }
}
