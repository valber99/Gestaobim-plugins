using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gestaobim.MaterialStudio.Core;

/// <summary>A GitHub release that is newer than the running version.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Url, string Notes);

/// <summary>Pure update rules: parsing the GitHub "latest release" answer and comparing versions.</summary>
public static class UpdateRules
{
    public const string LatestReleaseApi = "https://api.github.com/repos/valber99/Gestaobim-plugins/releases/latest";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>"v0.3.1" or "0.3.1-beta" become 0.3.1; anything unparseable becomes null.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) text = text.Substring(0, cut);
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>Returns the release when it is a published, non-prerelease version newer than <paramref name="current"/>.</summary>
    public static ReleaseInfo? ParseNewer(string json, Version current)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject release) return null;
            if (release["draft"]?.GetValue<bool>() == true || release["prerelease"]?.GetValue<bool>() == true) return null;
            var tag = release["tag_name"]?.GetValue<string>();
            var version = ParseVersion(tag);
            var url = release["html_url"]?.GetValue<string>();
            if (version is null || string.IsNullOrEmpty(url) || !url.StartsWith("https://github.com/", StringComparison.Ordinal))
                return null;
            if (version <= Normalize(current)) return null;
            return new ReleaseInfo(version, tag!, url, release["body"]?.GetValue<string>() ?? "");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>When we last asked GitHub and which version the user chose to ignore.</summary>
public sealed class UpdateState
{
    public DateTime LastCheckUtc { get; set; } = DateTime.MinValue;
    public string IgnoredTag { get; set; } = "";

    public bool Due(DateTime nowUtc) => nowUtc - LastCheckUtc >= UpdateRules.CheckInterval;

    private static string FilePath => Path.Combine(StoragePaths.Home, "update-check.json");

    public static UpdateState Load()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) is JsonObject data)
            {
                return new UpdateState
                {
                    LastCheckUtc = DateTime.TryParse(data["last_check_utc"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var when) ? when : DateTime.MinValue,
                    IgnoredTag = data["ignored_tag"]?.GetValue<string>() ?? "",
                };
            }
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
            Directory.CreateDirectory(StoragePaths.Home);
            var data = new JsonObject
            {
                ["last_check_utc"] = LastCheckUtc.ToString("o", CultureInfo.InvariantCulture),
                ["ignored_tag"] = IgnoredTag,
            };
            var temp = $"{FilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, data.ToJsonString(), new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Without the file we just check again next time.
        }
    }
}
