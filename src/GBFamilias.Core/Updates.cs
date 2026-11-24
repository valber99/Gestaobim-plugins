using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gestaobim.Familias.Core;

public sealed record ReleaseInfo(Version Version, string Tag, string Url);

/// <summary>
/// Regras de atualizacao. Os tres plugins Gestao BIM saem num instalador unico, numa release so, com tag "v<versao>"
/// (as tags antigas "familias-v" e "dwg-v" sao ignoradas). Vale a versao mais nova com esse prefixo.
/// </summary>
public static class UpdateRules
{
    public const string TagPrefix = "v";
    public const string ReleasesApi = "https://api.github.com/repos/valber99/Gestaobim-plugins/releases?per_page=30";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>"v0.4.1" vira 0.4.1. Tag sem o prefixo, ou que nao e versao, vira null.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var text = tag[TagPrefix.Length..];
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>A versao publicada mais nova (sem rascunho nem pre-lancamento) quando maior que <paramref name="current"/>.</summary>
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

/// <summary>Quando foi a ultima consulta de atualizacao.</summary>
public sealed class UpdateState
{
    public DateTime LastCheckUtc { get; set; } = DateTime.MinValue;

    public bool Due(DateTime nowUtc) => nowUtc - LastCheckUtc >= UpdateRules.CheckInterval;

    private static string FilePath => Path.Combine(FamiliasPaths.Home, "update-check.json");

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
            var data = new JsonObject { ["last_check_utc"] = LastCheckUtc.ToString("o", CultureInfo.InvariantCulture) };
            FamiliasPaths.WriteAtomic(FilePath, data.ToJsonString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Avisos sobre carregar uma familia salva em outra versao do Revit.</summary>
public static class FamilyCompat
{
    /// <summary>True se o Revit <paramref name="revitYear"/> consegue abrir a familia (ou quando a versao nao e conhecida).</summary>
    public static bool CanOpen(string? familyVersion, int revitYear)
    {
        if (string.IsNullOrWhiteSpace(familyVersion) || revitYear <= 0) return true;
        return !int.TryParse(familyVersion.TrimStart('~'), out var year) || year <= revitYear;
    }

    /// <summary>Aviso em portugues, ou null se nao ha o que avisar.</summary>
    public static string? Note(string? familyVersion, int revitYear)
    {
        if (string.IsNullOrWhiteSpace(familyVersion) || revitYear <= 0) return null; // sem o ano do Revit nao ha o que comparar
        var estimated = familyVersion.StartsWith('~');
        if (!int.TryParse(familyVersion.TrimStart('~'), out var year)) return null;
        var label = estimated ? $"cerca de {year}" : year.ToString();
        if (year > revitYear)
            return $"Esta familia foi salva no Revit {label}, mais novo que o seu ({revitYear}). O Revit nao consegue abri-la.";
        if (year < revitYear - 1)
            return $"Esta familia foi criada no Revit {label}. O Revit {revitYear} vai atualiza-la ao carregar (o arquivo original nao muda).";
        return null;
    }
}
