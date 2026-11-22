using System.Text;
using System.Text.Json.Nodes;

namespace Gestaobim.Familias.Core;

public static class FamiliasPaths
{
    /// <summary>Dados do usuario (configuracao, favoritos). GB_FAMILIAS_HOME serve aos testes.</summary>
    public static string Home =>
        Environment.GetEnvironmentVariable("GB_FAMILIAS_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestaoBIM", "Familias");

    /// <summary>Dados locais (fora do roaming): o catalogo sincronizado nao deve viajar com o perfil do usuario.</summary>
    public static string LocalHome =>
        Environment.GetEnvironmentVariable("GB_FAMILIAS_LOCAL") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GestaoBIM", "Familias");

    /// <summary>O catalogo sincronizado com o servidor (modo servidor).</summary>
    public static string CatalogDb => Path.Combine(LocalHome, "catalog.sqlite");

    /// <summary>Tokens de login (protegidos pelo Windows).</summary>
    public static string TokenFile => Path.Combine(LocalHome, "session.bin");

    /// <summary>Ferramentas compartilhadas entre os plugins Gestao BIM (mesma pasta do cadcraft-cli do DWG Export).</summary>
    public static string ToolsHome =>
        Environment.GetEnvironmentVariable("GB_TOOLS_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GestaoBIM", "Tools");

    /// <summary>Onde o pacote da biblioteca e extraido por padrao. Nao exige administrador para criar subpastas.</summary>
    public static string DefaultLibraryDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GestaoBIM", "Biblioteca");

    /// <summary>Procura o gbfam.exe: variavel de ambiente, pasta de ferramentas compartilhadas, ao lado do add-in.</summary>
    public static string? FindGbfam(string? addinDirectory)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("GB_FAMILIAS_GBFAM") is { Length: > 0 } env) candidates.Add(env);
        candidates.Add(Path.Combine(ToolsHome, "gbfam", "gbfam.exe"));
        if (!string.IsNullOrEmpty(addinDirectory))
        {
            candidates.Add(Path.Combine(addinDirectory, "gbfam.exe"));
            candidates.Add(Path.Combine(addinDirectory, "..", "..", "tools", "gbfam", "gbfam.exe"));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Junta a pasta da biblioteca com o caminho relativo gravado no banco. Recusa caminho absoluto ou que
    /// "fuja" da pasta (..): um banco adulterado nao pode fazer o plugin carregar arquivos de fora da biblioteca.
    /// </summary>
    public static string? ResolveFamilyPath(string? libraryRoot, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(relativePath)) return null;
        var sep = Path.DirectorySeparatorChar;
        var relative = relativePath.Replace('\\', sep).Replace('/', sep);
        // Letra de unidade ("C:") nao e caminho absoluto em Linux, mas nunca e valida num caminho relativo.
        if (Path.IsPathRooted(relative) || relative.StartsWith(sep) || relative.Contains(':')) return null;
        var root = Path.GetFullPath(libraryRoot);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSep = root.EndsWith(sep) ? root : root + sep;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(rootWithSep, comparison) ? full : null;
    }

    /// <summary>Gravacao atomica: escreve num temporario e troca, entao um travamento nunca deixa o arquivo pela metade.</summary>
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    /// <summary>Le um JSON; se estiver corrompido, guarda uma copia ao lado (nada se perde) e devolve null.</summary>
    public static JsonObject? ReadJsonOrQuarantine(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            try
            {
                File.Copy(path, $"{path}.corrompido-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            }
            catch (IOException)
            {
            }
            return null;
        }
    }
}
