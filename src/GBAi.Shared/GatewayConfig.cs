using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Gestaobim.Ai;

/// <summary>Uma chave de API de um provedor (a janela "Configurar IA" guarda cada uma com a DPAPI).</summary>
public sealed record ProviderKey(string Provider, string Key);

/// <summary>
/// O config.yaml do CLIProxyAPI. Os padroes do projeto sao de servidor (escuta em todas as interfaces, sem limite de
/// corpo, baixa um painel web do GitHub); aqui tudo e forcado para uso local. <see cref="Verify"/> le o arquivo de
/// volta e recusa qualquer divergencia, entao o plugin nao sobe o gateway com uma configuracao insegura.
/// </summary>
public static class GatewayConfig
{
    public const long MaxRequestBodyBytes = 64L * 1024 * 1024;   // cabe um render 4K e as imagens de referencia

    /// <summary>Provedores aceitos por chave de API: nome no config.yaml.</summary>
    public static readonly IReadOnlyDictionary<string, string> KeyProviders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini"] = "gemini",
        ["claude"] = "claude",
        ["codex"] = "codex",
    };

    public static string Build(int port, string clientToken, string managementKey, string authDir, IEnumerable<ProviderKey>? keys = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (clientToken.Length < 16) throw new ArgumentException("O token do cliente e curto demais.", nameof(clientToken));
        if (managementKey.Length < 16) throw new ArgumentException("A chave de gerenciamento e curta demais.", nameof(managementKey));

        var sb = new StringBuilder();
        sb.AppendLine("# Gerado pelo Gestao BIM a cada inicio. Nao edite: o plugin recusa subir se algo aqui divergir.");
        sb.AppendLine("config-version: 8");
        sb.AppendLine("server:");
        sb.AppendLine("  host: \"127.0.0.1\"");
        sb.AppendLine($"  port: {port.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"  max-request-body-bytes: {MaxRequestBodyBytes.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine("  discovery:");
        sb.AppendLine("    enabled: false");
        sb.AppendLine("  kv-store:");
        sb.AppendLine("    disabled: true");        // o bbolt trava o arquivo: dois Revits com a mesma pasta de contas nao subiriam
        sb.AppendLine("management:");
        sb.AppendLine("  allow-remote: false");
        sb.AppendLine($"  secret-key: {Quote(managementKey)}");
        sb.AppendLine("  disable-control-panel: true");
        sb.AppendLine("  disable-auto-update-panel: true");
        sb.AppendLine("access:");
        sb.AppendLine("  api-keys:");
        sb.AppendLine($"    - {Quote(clientToken)}");
        sb.AppendLine("oauth:");
        sb.AppendLine($"  auth-dir: {Quote(authDir.Replace('\\', '/'))}");
        sb.AppendLine("observability:");
        sb.AppendLine("  logs:");
        sb.AppendLine("    debug: false");
        sb.AppendLine("    logging-to-file: false");
        sb.AppendLine("    request-log: false");
        sb.AppendLine("  usage:");
        sb.AppendLine("    usage-statistics-enabled: false");
        sb.AppendLine("  pprof:");
        sb.AppendLine("    enable: false");
        sb.AppendLine("plugins:");
        sb.AppendLine("  enabled: false");

        var byProvider = (keys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k.Key) && KeyProviders.ContainsKey(k.Provider))
            .GroupBy(k => KeyProviders[k.Provider], StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (byProvider.Count > 0)
        {
            sb.AppendLine("api-keys:");
            foreach (var group in byProvider)
            {
                sb.AppendLine($"  {group.Key}:");
                sb.AppendLine($"    - name: {Quote("gb-" + group.Key)}");
                sb.AppendLine("      keys:");
                foreach (var key in group) sb.AppendLine($"        - api-key: {Quote(key.Key.Trim())}");
            }
        }
        return sb.ToString();
    }

    /// <summary>Confere o texto de um config.yaml. Devolve as divergencias (vazio = seguro).</summary>
    public static IReadOnlyList<string> Verify(string yaml, int expectedPort, string expectedClientToken, string expectedAuthDir)
    {
        var problems = new List<string>();
        var map = MiniYaml.Flatten(yaml);

        void Expect(string path, string value)
        {
            if (!map.TryGetValue(path, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
                problems.Add($"{path}: esperado '{value}', encontrado '{(map.TryGetValue(path, out var a) ? a : "(ausente)")}'");
        }

        Expect("config-version", "8");
        Expect("server.host", "127.0.0.1");
        Expect("server.port", expectedPort.ToString(CultureInfo.InvariantCulture));
        Expect("server.max-request-body-bytes", MaxRequestBodyBytes.ToString(CultureInfo.InvariantCulture));
        Expect("server.discovery.enabled", "false");
        Expect("server.kv-store.disabled", "true");
        Expect("management.allow-remote", "false");
        Expect("management.disable-control-panel", "true");
        Expect("management.disable-auto-update-panel", "true");
        Expect("access.api-keys[0]", expectedClientToken);
        if (map.ContainsKey("access.api-keys[1]")) problems.Add("access.api-keys: so um token por sessao e permitido");
        Expect("oauth.auth-dir", expectedAuthDir.Replace('\\', '/'));
        Expect("observability.logs.debug", "false");
        Expect("observability.logs.logging-to-file", "false");
        Expect("observability.logs.request-log", "false");
        Expect("observability.usage.usage-statistics-enabled", "false");
        Expect("observability.pprof.enable", "false");
        Expect("plugins.enabled", "false");
        if (!map.TryGetValue("management.secret-key", out var secret) || secret.Length < 16)
            problems.Add("management.secret-key: ausente ou curta (a API de gerenciamento ficaria desligada ou fraca)");
        if (map.Keys.Any(k => k.StartsWith("server.tls", StringComparison.Ordinal))) problems.Add("server.tls nao e usado no gateway local");
        return problems;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
