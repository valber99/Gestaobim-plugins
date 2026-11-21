using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gestaobim.DwgExport.Core;

public sealed record ReleaseInfo(Version Version, string Tag, string Url);

/// <summary>
/// Update rules for this plugin. The repository publishes several plugins, so each plugin's releases carry its own
/// tag prefix (here "dwg-v") and the latest one of that prefix is what counts.
/// </summary>
public static class UpdateRules
{
    public const string TagPrefix = "dwg-v";
    public const string ReleasesApi = "https://api.github.com/repos/valber99/Gestaobim-plugins/releases?per_page=30";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>"dwg-v0.2.1" becomes 0.2.1. A tag without the prefix, or not a version, becomes null.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var text = tag[TagPrefix.Length..];
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>The newest published, non-prerelease release of this plugin when it is newer than <paramref name="current"/>.</summary>
    public static ReleaseInfo? ParseNewer(string json, Version current)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray releases) return null;
            ReleaseInfo? best = null;
            foreach (var node in releases.OfType<JsonObject>())
            {
                if (node["draft"]?.GetValue<bool>() == true || node["prerelease"]?.GetValue<bool>() == true) continue;
                var tag = node["tag_name"]?.GetValue<string>();
                var url = node["html_url"]?.GetValue<string>();
                var version = ParseVersion(tag);
                if (version is null || string.IsNullOrEmpty(url) || !url.StartsWith("https://github.com/", StringComparison.Ordinal)) continue;
                if (best is null || version > best.Version) best = new ReleaseInfo(version, tag!, url);
            }
            return best is not null && best.Version > Normalize(current) ? best : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>When the last check happened and which version the user chose to ignore.</summary>
public sealed class UpdateState
{
    public DateTime LastCheckUtc { get; set; } = DateTime.MinValue;

    public bool Due(DateTime nowUtc) => nowUtc - LastCheckUtc >= UpdateRules.CheckInterval;

    private static string FilePath => Path.Combine(DwgPaths.Home, "update-check.json");

    public static UpdateState Load()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) is JsonObject data
                && DateTime.TryParse(data["last_check_utc"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                return new UpdateState { LastCheckUtc = when };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }
        return new UpdateState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DwgPaths.Home);
            var data = new JsonObject { ["last_check_utc"] = LastCheckUtc.ToString("o", CultureInfo.InvariantCulture) };
            File.WriteAllText(FilePath, data.ToJsonString(), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
