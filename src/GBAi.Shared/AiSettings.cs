using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gestaobim.Ai;

/// <summary>
/// Configuracoes de IA do usuario: modelo de cada tarefa e chaves de API. As chaves ficam em arquivo cifrado com a DPAPI
/// e nunca voltam para a interface: ela so ve "configurada / nao configurada".
/// </summary>
public sealed class AiSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Modelo que planeja (texto para JSON). Vazio = escolher o primeiro adequado da lista do gateway.</summary>
    public string PlanModel { get; set; } = "";

    /// <summary>Modelo que gera imagens. Vazio = escolher um modelo de imagem da lista.</summary>
    public string ImageModel { get; set; } = "";

    /// <summary>O usuario ja leu o aviso sobre usar a assinatura (OAuth) em vez de chave de API.</summary>
    public bool OAuthRiskAcknowledged { get; set; }

    /// <summary>O usuario ja leu o aviso de que as imagens do projeto vao para um provedor externo.</summary>
    public bool PrivacyAcknowledged { get; set; }

    /// <summary>Remove fabricante e modelo do manifesto enviado ao provedor.</summary>
    public bool StripManufacturers { get; set; }

    [JsonIgnore]
    public Dictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static string SettingsFile => Path.Combine(GbAiPaths.SettingsDir, "settings.json");
    private static string KeysFile => Path.Combine(GbAiPaths.SettingsDir, "keys.dat");

    public bool HasKey(string provider) => Keys.TryGetValue(provider, out var k) && !string.IsNullOrWhiteSpace(k);

    public IReadOnlyList<ProviderKey> ProviderKeys() =>
        Keys.Where(k => !string.IsNullOrWhiteSpace(k.Value)).Select(k => new ProviderKey(k.Key, k.Value)).ToList();

    public static AiSettings Load()
    {
        var settings = new AiSettings();
        try
        {
            if (File.Exists(SettingsFile))
                settings = JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(SettingsFile, Encoding.UTF8), Json) ?? new AiSettings();
        }
        catch (JsonException)
        {
            QuarantineCorrupt(SettingsFile);
        }
        catch (IOException)
        {
        }
        try
        {
            if (File.Exists(KeysFile))
            {
                var plain = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(KeysFile)));
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [])
                    settings.Keys[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            QuarantineCorrupt(KeysFile);   // outro usuario do Windows ou arquivo danificado: guarda ao lado, nunca sobrescreve
        }
        catch (IOException)
        {
        }
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(GbAiPaths.SettingsDir);
        WriteAtomic(SettingsFile, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json)));
        var keys = Keys.Where(k => !string.IsNullOrWhiteSpace(k.Value)).ToDictionary(k => k.Key, k => k.Value);
        WriteAtomic(KeysFile, Dpapi.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(keys))));
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static void QuarantineCorrupt(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, $"{path}.corrompido-{DateTime.Now:yyyyMMdd-HHmmss}");
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Escolhe o primeiro modelo adequado quando o usuario nao fixou um.</summary>
    public static string PickModel(IReadOnlyList<string> models, bool image)
    {
        if (image)
            return models.FirstOrDefault(m => m.Contains("image", StringComparison.OrdinalIgnoreCase)
                                              && m.Contains("pro", StringComparison.OrdinalIgnoreCase))
                   ?? models.FirstOrDefault(m => m.Contains("image", StringComparison.OrdinalIgnoreCase))
                   ?? "";
        foreach (var family in new[] { "claude", "gpt", "gemini" })
        {
            var hit = models.FirstOrDefault(m => m.Contains(family, StringComparison.OrdinalIgnoreCase)
                                                 && !m.Contains("image", StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return models.FirstOrDefault(m => !m.Contains("image", StringComparison.OrdinalIgnoreCase)) ?? "";
    }
}
