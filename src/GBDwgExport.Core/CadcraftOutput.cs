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
    /// The paper-space layout of a sheet exported by Revit, from the JSON lines of
    /// <c>run FILE --cmd getvar {"name":"INSUNITS"} --cmd layout.list {}</c>: the layout with the most viewports
    /// (Revit puts the sheet in one layout and leaves the other empty). Null when no layout holds a viewport.
    /// </summary>
    public static (string Layout, int InsUnits)? ParseSheetLayout(string output)
    {
        var insUnits = 0;
        string? best = null;
        var bestCount = 0;
        foreach (var node in JsonLines(output))
        {
            if (node is JsonObject variable && variable["name"]?.GetValue<string>() == "INSUNITS"
                && variable["value"] is JsonValue value && value.TryGetValue<int>(out var code))
                insUnits = code;
            if (node is not JsonArray layouts) continue;
            foreach (var layout in layouts.OfType<JsonObject>())
            {
                var count = (layout["viewports"] as JsonArray)?.Count ?? 0;
                var name = layout["name"]?.GetValue<string>();
                if (name is { Length: > 0 } && count > bestCount)
                {
                    best = name;
                    bestCount = count;
                }
            }
        }
        return best is null ? null : (best, insUnits);
    }

    /// <summary>
    /// The viewports of a layout, from <c>entities {"type":"viewport"}</c> run in that layout. The one with id 1 is the
    /// layout's own overall viewport; the others show views, each centred on a model-space point.
    /// </summary>
    public static (string? Overall, IReadOnlyList<ViewportCenter> Views) ParseViewports(string output)
    {
        string? overall = null;
        var views = new List<ViewportCenter>();
        var result = JsonLines(output).OfType<JsonObject>().LastOrDefault(node => node["entities"] is JsonArray);
        foreach (var entity in (result?["entities"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!string.Equals(entity["type"]?.GetValue<string>(), "Viewport", StringComparison.OrdinalIgnoreCase)) continue;
            var handle = entity["handle"]?.GetValue<string>();
            if (string.IsNullOrEmpty(handle) || entity["geometry"] is not JsonObject geometry) continue;
            if (geometry["id"] is JsonValue id && id.TryGetValue<int>(out var number) && number == 1)
            {
                overall = handle;
                continue;
            }
            if (geometry["viewCenter"] is not JsonObject center) continue;
            var x = center["x"]?.GetValue<double>();
            var y = center["y"]?.GetValue<double>();
            if (x is null || y is null) continue;
            views.Add(new ViewportCenter(handle, x.Value, y.Value));
        }
        return (overall, views);
    }

    /// <summary>Each line the tool prints that parses as JSON; anything else is skipped.</summary>
    private static IEnumerable<JsonNode> JsonLines(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || (line[0] != '{' && line[0] != '[')) continue;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (node is not null) yield return node;
        }
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
