using System.Text.Json.Nodes;

namespace Gestaobim.DwgExport.Core;

/// <summary>Reads what cadcraft-cli prints. Kept apart from the process code so it is tested with plain text.</summary>
public static class CadcraftOutput
{
    /// <summary>The model-space rectangle from <c>cadcraft-cli info --compact</c>, or null when the drawing is empty.</summary>
    public static (double X0, double Y0, double X1, double Y1)? ParseExtents(string infoJson)
    {
        if (JsonNode.Parse(infoJson) is not JsonObject info || info["extents"] is not JsonArray { Count: 2 } extents) return null;
        if (extents[0] is not JsonArray { Count: >= 2 } low || extents[1] is not JsonArray { Count: >= 2 } high) return null;
        var values = new[] { low[0], low[1], high[0], high[1] }.Select(node => node?.GetValue<double>()).ToArray();
        if (values.Any(value => value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))) return null;
        if (values[2] <= values[0] || values[3] <= values[1]) return null;
        return (values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value);
    }

    public sealed record Summary(int Entities, int Layers, int Blocks, IReadOnlyList<string> Layouts);

    public static Summary? ParseSummary(string infoJson)
    {
        if (JsonNode.Parse(infoJson) is not JsonObject info) return null;
        var layouts = (info["layouts"] as JsonArray)?.Select(node => node?.GetValue<string>() ?? "").ToList() ?? [];
        return new Summary(
            info["entityCount"]?.GetValue<int>() ?? 0,
            (info["layers"] as JsonArray)?.Count ?? 0,
            (info["blocks"] as JsonArray)?.Count ?? 0,
            layouts);
    }

    /// <summary>
    /// Content the tool reports it could not keep, from its standard error. The DWG writer says so itself,
    /// for example "Not saved in the DWG file (...): 114 ARC_DIMENSION".
    /// </summary>
    public static IReadOnlyList<string> ParseLosses(string standardError) =>
        standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("Not saved", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("left out", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("warning", StringComparison.OrdinalIgnoreCase))
            .ToList();
}
