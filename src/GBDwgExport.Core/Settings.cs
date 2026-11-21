using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gestaobim.DwgExport.Core;

public static class DwgPaths
{
    public const string HomeEnvironmentVariable = "GB_DWG_EXPORT_HOME";
    public const string CadcraftEnvironmentVariable = "GB_CADCRAFT_CLI";
    public const string CliName = "cadcraft-cli.exe";

    public static string Home =>
        Environment.GetEnvironmentVariable(HomeEnvironmentVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestaoBIM", "DwgExport");

    public static string SettingsFile => Path.Combine(Home, "settings.json");

    /// <summary>Shared tools folder, where the installer puts the unmodified cadcraft-cli.</summary>
    public static string ToolsHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestaoBIM", "Tools");

    /// <summary>Looks for the CLI: the environment override, the shared tools folder, then next to the add-in.</summary>
    public static string? FindCadcraft(string? addinFolder = null)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(CadcraftEnvironmentVariable) is { Length: > 0 } fromEnvironment)
            candidates.Add(fromEnvironment);
        candidates.Add(Path.Combine(ToolsHome, "cadcraft", CliName));
        if (!string.IsNullOrEmpty(addinFolder))
            candidates.Add(Path.Combine(addinFolder, "cadcraft", CliName));
        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>What the user last chose in the export window.</summary>
public sealed class ExportSettings
{
    public string Folder { get; set; } = "";
    public bool Dxf { get; set; }
    public NamePattern Pattern { get; set; } = NamePattern.NumberAndName;
    public OutputMode Mode { get; set; } = OutputMode.SeparateFiles;
    public string Paper { get; set; } = "A1";
    public bool Landscape { get; set; } = true;
    /// <summary>Name of a predefined DWG export setup of the project, or empty for the plugin defaults.</summary>
    public string RevitSetup { get; set; } = "";
    public bool KeepIndividualFiles { get; set; } = true;
    public bool OpenFolder { get; set; } = true;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static ExportSettings Load(string? path = null)
    {
        try
        {
            var text = File.ReadAllText(path ?? DwgPaths.SettingsFile, Encoding.UTF8);
            return JsonSerializer.Deserialize<ExportSettings>(text, Options) ?? new ExportSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new ExportSettings();
        }
    }

    public void Save(string? path = null)
    {
        var file = path ?? DwgPaths.SettingsFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A setting that does not save is only a convenience lost.
        }
    }
}

public sealed class TransferException(string message) : Exception(message);

/// <summary>Packs the settings into a .zip with a manifest, and reads one back, so they move between machines.</summary>
public static class SettingsTransfer
{
    public const string Product = "GBDwgExport";
    private const string ManifestEntry = "manifest.json";
    private const string SettingsEntry = "settings.json";

    public static void Export(ExportSettings settings, string zipPath, string version)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifest = new JsonObject { ["product"] = Product, ["version"] = version, ["created_utc"] = DateTime.UtcNow.ToString("o") };
        Write(archive, ManifestEntry, manifest.ToJsonString());
        var temp = Path.GetTempFileName();
        try
        {
            settings.Save(temp);
            Write(archive, SettingsEntry, File.ReadAllText(temp, Encoding.UTF8));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static ExportSettings Import(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var manifestText = Read(archive, ManifestEntry);
            if (JsonNode.Parse(manifestText) is not JsonObject manifest || manifest["product"]?.GetValue<string>() != Product)
                throw new TransferException("Este arquivo nao e um pacote de configuracoes do GB DWG Export.");

            var settings = JsonSerializer.Deserialize<ExportSettings>(Read(archive, SettingsEntry),
                new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
            return settings ?? throw new TransferException("O arquivo de configuracoes esta vazio.");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or IOException)
        {
            throw new TransferException($"Nao foi possivel ler o pacote: {error.Message}");
        }
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string Read(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new TransferException($"Falta '{name}' no pacote.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
