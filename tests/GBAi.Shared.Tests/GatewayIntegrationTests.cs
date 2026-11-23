using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gestaobim.Ai;
using Xunit;

namespace GBAi.Shared.Tests;

internal static class RealProxy
{
    /// <summary>build/cache/cliproxy-x/cli-proxy-api.exe (a v8.1.1 extraida do .zip da release), ou a que GBAI_TEST_PROXY apontar.</summary>
    public static string? Find()
    {
        var env = Environment.GetEnvironmentVariable("GBAI_TEST_PROXY");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "build", "cache", "cliproxy-x", "cli-proxy-api.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static string? RepoCache()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "build", "cache"))) return Path.Combine(dir.FullName, "build", "cache");
        return null;
    }
}

/// <summary>Teste que precisa do binario real. Sem ele (por exemplo no CI sem o download) o teste e pulado, nao aprovado.</summary>
public sealed class RealGatewayFactAttribute : FactAttribute
{
    public RealGatewayFactAttribute()
    {
        if (RealProxy.Find() is null) Skip = "cli-proxy-api.exe nao encontrado em build/cache/cliproxy-x (veja docs/PLANO-GB-IA.md)";
    }
}

/// <summary>A fase 1 do plano: o contrato de seguranca do gateway, verificado no CLIProxyAPI de verdade.</summary>
public class GatewayIntegrationTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gbai-gw-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevHome = Environment.GetEnvironmentVariable("GBAI_HOME");
    private readonly string? _prevExe = Environment.GetEnvironmentVariable("GBAI_PROXY_EXE");

    public GatewayIntegrationTests()
    {
        Environment.SetEnvironmentVariable("GBAI_HOME", _home);
        Environment.SetEnvironmentVariable("GBAI_PROXY_EXE", RealProxy.Find());
    }

    public void Dispose()
    {
        GatewayHost.Stop();
        Environment.SetEnvironmentVariable("GBAI_HOME", _prevHome);
        Environment.SetEnvironmentVariable("GBAI_PROXY_EXE", _prevExe);
        for (var i = 0; i < 5; i++)
        {
            try { Directory.Delete(_home, true); break; } catch (Exception) { Thread.Sleep(300); }
        }
    }

    [RealGatewayFact]
    public async Task GatewayHonorsTheSecurityContract()
    {
        var endpoint = await GatewayHost.EnsureStartedAsync();
        var uri = new Uri(endpoint.BaseUrl);
        Assert.Equal("127.0.0.1", uri.Host);

        // 401 sem token, 200 com token, e o hash do binario e o aprovado.
        var health = await GatewayHost.CheckHealthAsync(endpoint);
        Assert.Equal(401, health.StatusWithoutToken);
        Assert.Equal(200, health.StatusWithToken);
        Assert.Equal(ProxyPin.ExeSha256, health.ExeSha256);

        // Token errado tambem e recusado.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var wrong = new HttpRequestMessage(HttpMethod.Get, endpoint.BaseUrl + "/v1/models");
        wrong.Headers.Authorization = new("Bearer", "token-errado-token-errado");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

        // Corpo acima do limite: 413 (alerta GHSA-4w5v-m7vx-jpmh sem autenticacao: o limite vale antes de qualquer outra coisa).
        using var big = new HttpRequestMessage(HttpMethod.Post, endpoint.BaseUrl + "/v1/chat/completions")
        {
            Content = new ByteArrayContent(new byte[(int)GatewayConfig.MaxRequestBodyBytes + 1]),
        };
        big.Headers.Authorization = new("Bearer", endpoint.ClientToken);
        big.Content.Headers.ContentType = new("application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await http.SendAsync(big)).StatusCode);

        // A API de gerenciamento exige a chave dela; o painel web baixado do GitHub esta desligado.
        using var noKey = new HttpRequestMessage(HttpMethod.Get, endpoint.BaseUrl + "/v0/management/auth-files");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(noKey)).StatusCode);
        using var withKey = new HttpRequestMessage(HttpMethod.Get, endpoint.BaseUrl + "/v0/management/auth-files");
        withKey.Headers.Authorization = new("Bearer", endpoint.ManagementKey);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(withKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(endpoint.BaseUrl + "/management.html")).StatusCode);

        // Porta recusada em qualquer outro IP do computador (a rede local nao alcanca o gateway).
        var lan = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .ToList();
        foreach (var address in lan)
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(address, uri.Port, cts.Token).AsTask());
        }

        // A pasta de contas e a de execucao so dao acesso ao usuario e ao SYSTEM, sem heranca.
        foreach (var dir in new[] { GbAiPaths.AuthDir, GbAiPaths.RunDir, GbAiPaths.DataDir })
        {
            var rules = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.All(rules, r => Assert.False(r.IsInherited, $"{dir}: regra herdada para {r.IdentityReference}"));
            var allowed = rules.Select(r => r.IdentityReference.Value).Distinct().ToList();
            Assert.Equal(2, allowed.Count);
            Assert.Contains(WindowsIdentity.GetCurrent().User!.Value, allowed);
            Assert.Contains("S-1-5-18", allowed);
        }

        // A configuracao em execucao e a endurecida (le o arquivo do disco, que guarda o token).
        var config = Directory.GetFiles(GbAiPaths.RunDir, "config-*.yaml").Single();
        Assert.Empty(GatewayConfig.Verify(File.ReadAllText(config), uri.Port, endpoint.ClientToken, GbAiPaths.AuthDir));

        // Segunda chamada reaproveita o mesmo processo; os dois plugins dividem um gateway.
        var again = await GatewayHost.EnsureStartedAsync();
        Assert.Equal(endpoint.Pid, again.Pid);
        Assert.Equal(endpoint.ClientToken, again.ClientToken);

        // Relatorio de saude (artefato da fase 1).
        var report = new JsonObject
        {
            ["bind"] = health.Bind,
            ["port"] = health.Port,
            ["status_without_token"] = health.StatusWithoutToken,
            ["status_with_token"] = health.StatusWithToken,
            ["body_limit_status"] = 413,
            ["lan_addresses_refused"] = lan.Count,
            ["proxy_version"] = health.Version,
            ["exe_sha256"] = health.ExeSha256,
            ["zip_sha256"] = ProxyPin.ZipSha256,
        };
        if (RealProxy.RepoCache() is { } cache)
            File.WriteAllText(Path.Combine(cache, "gateway-health.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Parar encerra o processo e apaga o config.yaml que tinha o token.
        GatewayHost.Stop();
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(endpoint.Pid).HasExited ? throw new ArgumentException() : 0);
        Assert.Empty(Directory.GetFiles(GbAiPaths.RunDir, "config-*.yaml"));
        Assert.Null(GatewayHost.Current());
    }

    [RealGatewayFact]
    public async Task ARestartedGatewayGetsNewSecrets()
    {
        var first = await GatewayHost.EnsureStartedAsync();
        var second = await GatewayHost.RestartAsync();
        Assert.NotEqual(first.Pid, second.Pid);
        Assert.NotEqual(first.ClientToken, second.ClientToken);
        Assert.NotEqual(first.ManagementKey, second.ManagementKey);
        using var http = new HttpClient();
        using var oldToken = new HttpRequestMessage(HttpMethod.Get, second.BaseUrl + "/v1/models");
        oldToken.Headers.Authorization = new("Bearer", first.ClientToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(oldToken)).StatusCode);
    }

    [RealGatewayFact]
    public async Task ProviderApiKeysAreAcceptedByTheGateway()
    {
        var endpoint = await GatewayHost.EnsureStartedAsync([new ProviderKey("gemini", "AIzaSy-chave-de-teste-0000000000000000")]);
        using var client = new AiClient(endpoint);
        var models = await client.ListModelsAsync();
        Assert.Contains(models, m => m.Contains("gemini", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual("", AiSettings.PickModel(models, image: true) + AiSettings.PickModel(models, image: false));
    }

    [RealGatewayFact]
    public async Task TwoGatewaysShareOneAuthFolderWithoutLocking()
    {
        // Dois Revits abertos ao mesmo tempo: cada um com o seu gateway e a mesma pasta de contas.
        var exe = RealProxy.Find()!;
        GbAiPaths.EnsurePrivate(GbAiPaths.AuthDir);
        GbAiPaths.EnsurePrivate(GbAiPaths.RunDir);
        var processes = new List<Process>();
        try
        {
            var ports = new List<int>();
            for (var i = 0; i < 2; i++)
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                var port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                ports.Add(port);
                var cfg = Path.Combine(GbAiPaths.RunDir, $"two-{i}.yaml");
                File.WriteAllText(cfg, GatewayConfig.Build(port, "tokentokentoken" + i + "0000", "mgmtmgmtmgmtmgmt" + i + "0", GbAiPaths.AuthDir));
                var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-config", cfg, "-local-model" }) info.ArgumentList.Add(a);
                processes.Add(Process.Start(info)!);
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (var i = 0; i < 2; i++)
            {
                var ok = false;
                for (var t = 0; t < 60 && !ok; t++)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{ports[i]}/v1/models");
                        req.Headers.Authorization = new("Bearer", "tokentokentoken" + i + "0000");
                        ok = (await http.SendAsync(req)).StatusCode == HttpStatusCode.OK;
                    }
                    catch (Exception) { }
                    if (!ok) await Task.Delay(250);
                }
                Assert.True(ok, $"o gateway {i} nao subiu com a pasta de contas em uso pelo outro");
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Kill(true); p.Dispose(); } catch (Exception) { }
            }
        }
    }

    [RealGatewayFact]
    public async Task RefusesAnUnapprovedExecutable()
    {
        var fake = Path.Combine(_home, "falso", "cli-proxy-api.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
        File.Copy(Environment.ProcessPath!, fake);   // qualquer outro exe: o SHA-256 nao bate
        Environment.SetEnvironmentVariable("GBAI_PROXY_EXE", fake);
        var error = await Assert.ThrowsAsync<GatewayException>(() => GatewayHost.EnsureStartedAsync());
        Assert.Contains("SHA-256", error.Message);
        Assert.Null(GatewayHost.Current());
    }

    [Fact]
    public async Task MissingExecutableGivesAClearMessage()
    {
        Environment.SetEnvironmentVariable("GBAI_PROXY_EXE", Path.Combine(_home, "nao-existe.exe"));
        var error = await Assert.ThrowsAsync<GatewayException>(() => GatewayHost.EnsureStartedAsync());
        Assert.Contains("instalador", error.Message);
    }

    [RealGatewayFact]
    public void EveryOAuthLoginFlagExistsInTheApprovedBinary()
    {
        var info = new ProcessStartInfo(RealProxy.Find()!, "-h") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        foreach (var provider in OAuthLogin.Providers) Assert.Contains(provider.Flag.TrimStart('-') + "\n", text.Replace("\r", ""));
        Assert.Contains("-no-browser", text);
        Assert.Contains("-local-model", text);
        Assert.Contains("-config", text);
    }

    [RealGatewayFact]
    public void TheApprovedBinaryMatchesThePin() => Assert.Equal(ProxyPin.ExeSha256, GatewayHost.Sha256Of(RealProxy.Find()!));
}
