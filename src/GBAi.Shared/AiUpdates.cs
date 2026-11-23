using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Gestaobim.Ai;

public sealed record ReleaseInfo(Version Version, string Tag, string Url);

/// <summary>
/// Regras de atualizacao dos plugins de IA: as mesmas dos outros (uma release "v&lt;versao&gt;" do instalador unico, a mais nova que a
/// instalada, sem rascunho nem pre-lancamento), com o proprio arquivo de ultima consulta.
/// </summary>
public static class AiUpdateRules
{
    public const string TagPrefix = "v";
    public const string ReleasesApi = "https://api.github.com/repos/valber99/Gestaobim-plugins/releases?per_page=30";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var text = tag[TagPrefix.Length..];
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    public static ReleaseInfo? ParseNewer(string json, Version current)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonArray releases) return null;
            ReleaseInfo? best = null;
            foreach (var node in releases)
            {
                if (node is not JsonObject obj) continue;
                if (obj["draft"]?.GetValue<bool>() == true || obj["prerelease"]?.GetValue<bool>() == true) continue;
                var tag = obj["tag_name"]?.GetValue<string>();
                var url = obj["html_url"]?.GetValue<string>();
                var version = ParseVersion(tag);
                if (version is null || string.IsNullOrEmpty(url) || !url.StartsWith("https://github.com/", StringComparison.Ordinal)) continue;
                if (best is null || version > best.Version) best = new ReleaseInfo(version, tag!, url);
            }
            return best is not null && best.Version > Normalize(current) ? best : null;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Quando foi a ultima consulta (no maximo uma por dia).</summary>
public sealed class AiUpdateState
{
    public DateTime LastCheckUtc { get; set; } = DateTime.MinValue;

    public bool Due(DateTime nowUtc) => nowUtc - LastCheckUtc >= AiUpdateRules.CheckInterval;

    private static string FilePath => Path.Combine(GbAiPaths.SettingsDir, "update-check.json");

    public static AiUpdateState Load()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) is JsonObject data
                && DateTime.TryParse(data["last_check_utc"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
                return new AiUpdateState { LastCheckUtc = when };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        return new AiUpdateState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(GbAiPaths.SettingsDir);
            var data = new JsonObject { ["last_check_utc"] = LastCheckUtc.ToString("o", CultureInfo.InvariantCulture) };
            File.WriteAllText(FilePath, data.ToJsonString(), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
