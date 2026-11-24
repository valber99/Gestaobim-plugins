using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Gestaobim.Ai;

public sealed class GatewayException(string message) : Exception(message);

/// <summary>A versao do CLIProxyAPI que o Gestao BIM aceita. Trocar de versao exige trocar estes valores (e revisar).</summary>
public static class ProxyPin
{
    public const string Version = "8.1.1";

    /// <summary>SHA-256 do cli-proxy-api.exe da release v8.1.1 (windows_amd64). Conferido antes de cada inicio.</summary>
    public const string ExeSha256 = "aa4faf13862209c94b1556236ddb193fe3608614e0d19c5a7e6b6f07db07efff";

    /// <summary>SHA-256 do CLIProxyAPI_8.1.1_windows_amd64.zip (checksums.txt da release). Conferido ao empacotar.</summary>
    public const string ZipSha256 = "f345c06313e487e474baf03fda1e3ec8a76c26bf2e3f57f3c7dcbd60857c1b2c";
}

/// <summary>Como entrar no gateway desta sessao do Revit.</summary>
public sealed record GatewayEndpoint(string BaseUrl, string ClientToken, string ManagementKey, int Pid);

/// <summary>O que o teste de saude conferiu (vira o gateway-health.json).</summary>
public sealed record GatewayHealth(string Bind, int Port, int StatusWithoutToken, int StatusWithToken, string ExeSha256, string Version);

/// <summary>
/// Sobe o CLIProxyAPI como processo filho, so em 127.0.0.1, com token por sessao e o config.yaml endurecido.
/// O processo entra num Job Object do Windows que o encerra se o Revit morrer. Os dois plugins de IA (cada um com a sua copia
/// desta classe dentro da propria DLL) dividem um unico gateway por sessao: o endereco fica nos dados do AppDomain.
/// </summary>
public static class GatewayHost
{
    private const string ShareKey = "GestaoBIM.Ai.Gateway";
    private const string MutexName = @"Local\GestaoBIM.Ai.GatewayStart";

    private static Process? _process;
    private static IntPtr _job;

    public static Task<GatewayEndpoint> EnsureStartedAsync(IEnumerable<ProviderKey>? keys = null, CancellationToken cancel = default) =>
        Task.Run(() => EnsureStarted(keys, cancel), cancel);

    /// <summary>Reinicia (por exemplo depois de trocar uma chave de API).</summary>
    public static Task<GatewayEndpoint> RestartAsync(IEnumerable<ProviderKey>? keys = null, CancellationToken cancel = default) =>
        Task.Run(() =>
        {
            Stop();
            return EnsureStarted(keys, cancel);
        }, cancel);

    public static GatewayEndpoint? Current()
    {
        if (AppDomain.CurrentDomain.GetData(ShareKey) is not string[] { Length: 4 } data) return null;
        if (!int.TryParse(data[3], out var pid) || !IsAlive(pid)) return null;
        return new GatewayEndpoint(data[0], data[1], data[2], pid);
    }

    public static GatewayEndpoint EnsureStarted(IEnumerable<ProviderKey>? keys, CancellationToken cancel)
    {
        using var mutex = new Mutex(false, MutexName);
        Acquire(mutex, cancel);
        try
        {
            if (Current() is { } running && Ping(running)) return running;
            return Start(keys, cancel);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static void Stop()
    {
        using var mutex = new Mutex(false, MutexName);
        var acquired = TryAcquire(mutex, TimeSpan.FromSeconds(2));
        try
        {
            if (AppDomain.CurrentDomain.GetData(ShareKey) is string[] { Length: 4 } data && int.TryParse(data[3], out var pid))
                KillPid(pid);
            AppDomain.CurrentDomain.SetData(ShareKey, null);
            try { _process?.Dispose(); } catch (Exception) { }
            _process = null;
            DeleteConfigsOf(Environment.ProcessId);
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    /// <summary>Confere o contrato de seguranca do gateway em execucao (usado pelo teste e pelo botao "Testar conexao").</summary>
    public static async Task<GatewayHealth> CheckHealthAsync(GatewayEndpoint endpoint, CancellationToken cancel = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var uri = new Uri(endpoint.BaseUrl);
        using var noToken = await http.GetAsync(new Uri(uri, "/v1/models"), cancel).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/v1/models"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.ClientToken);
        using var withToken = await http.SendAsync(request, cancel).ConfigureAwait(false);
        return new GatewayHealth(uri.Host, uri.Port, (int)noToken.StatusCode, (int)withToken.StatusCode, Sha256Of(GbAiPaths.ProxyExe), ProxyPin.Version);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static GatewayEndpoint Start(IEnumerable<ProviderKey>? keys, CancellationToken cancel)
    {
        var exe = GbAiPaths.ProxyExe;
        if (!File.Exists(exe)) throw new GatewayException($"O gateway de IA nao foi encontrado: {exe}. Rode o instalador do Gestao BIM de novo.");
        var actual = Sha256Of(exe);
        if (!string.Equals(actual, ProxyPin.ExeSha256, StringComparison.OrdinalIgnoreCase))
            throw new GatewayException("O gateway de IA nao e a versao aprovada (SHA-256 diferente). Rode o instalador do Gestao BIM de novo.");

        GbAiPaths.EnsurePrivate(GbAiPaths.DataDir);
        GbAiPaths.EnsurePrivate(GbAiPaths.AuthDir);
        GbAiPaths.EnsurePrivate(GbAiPaths.RunDir);
        DeleteStaleConfigs();

        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var clientToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var managementKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var configPath = Path.Combine(GbAiPaths.RunDir, $"config-{Environment.ProcessId}-{port}.yaml");
            var authDir = GbAiPaths.AuthDir;
            File.WriteAllText(configPath, GatewayConfig.Build(port, clientToken, managementKey, authDir, keys), new UTF8Encoding(false));
            var problems = GatewayConfig.Verify(File.ReadAllText(configPath, Encoding.UTF8), port, clientToken, authDir);
            if (problems.Count > 0)
            {
                TryDelete(configPath);
                throw new GatewayException("Configuracao do gateway recusada: " + string.Join("; ", problems));
            }

            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = GbAiPaths.RunDir,
            };
            foreach (var arg in new[] { "-config", configPath, "-local-model" }) info.ArgumentList.Add(arg);
            var process = Process.Start(info) ?? throw new GatewayException("Nao foi possivel iniciar o gateway de IA.");
            var tail = new Queue<string>();
            void Collect(string? line)
            {
                if (line is null) return;
                lock (tail)
                {
                    tail.Enqueue(line);
                    while (tail.Count > 12) tail.Dequeue();
                }
            }
            process.OutputDataReceived += (_, e) => Collect(e.Data);
            process.ErrorDataReceived += (_, e) => Collect(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AssignToJob(process);

            var endpoint = new GatewayEndpoint($"http://127.0.0.1:{port}", clientToken, managementKey, process.Id);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        string detail;
                        lock (tail) detail = string.Join(" | ", tail);
                        // A porta pode ter sido tomada por outro programa entre escolher e escutar: uma nova tentativa resolve.
                        if (attempt < 3 && detail.Contains("address", StringComparison.OrdinalIgnoreCase)) break;
                        throw new GatewayException("O gateway de IA encerrou ao iniciar. " + detail);
                    }
                    if (Ping(endpoint)) break;
                    if (DateTime.UtcNow > deadline) throw new GatewayException("O gateway de IA demorou demais para ficar pronto.");
                    Thread.Sleep(150);
                }
                if (process.HasExited) { TryDelete(configPath); continue; }

                // Contrato de seguranca: sem token tem de ser 401.
                var health = CheckHealthAsync(endpoint, cancel).GetAwaiter().GetResult();
                if (health.StatusWithoutToken != 401)
                    throw new GatewayException($"O gateway respondeu {health.StatusWithoutToken} sem token (esperado 401). Ele foi encerrado.");

                _process = process;
                AppDomain.CurrentDomain.SetData(ShareKey, new[] { endpoint.BaseUrl, endpoint.ClientToken, endpoint.ManagementKey, endpoint.Pid.ToString() });
                return endpoint;
            }
            catch (Exception)
            {
                KillPid(process.Id);
                TryDelete(configPath);
                throw;
            }
        }
    }

    /// <summary>Um Revit que caiu segurando o mutex o deixa abandonado: a posse passa para este processo, e o trabalho segue.</summary>
    private static void Acquire(Mutex mutex, CancellationToken cancel)
    {
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (TryAcquire(mutex, TimeSpan.FromMilliseconds(200))) return;
        }
    }

    private static bool TryAcquire(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool Ping(GatewayEndpoint endpoint)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.BaseUrl + "/v1/models");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.ClientToken);
            using var response = http.Send(request);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Contains("cli-proxy-api", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void KillPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited && p.ProcessName.Contains("cli-proxy-api", StringComparison.OrdinalIgnoreCase))
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
            }
        }
        catch (Exception)
        {
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    private static void DeleteConfigsOf(int revitPid)
    {
        try
        {
            foreach (var file in Directory.GetFiles(GbAiPaths.RunDir, $"config-{revitPid}-*.yaml")) TryDelete(file);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Apaga os config.yaml (que guardam o token) de sessoes que ja acabaram.</summary>
    private static void DeleteStaleConfigs()
    {
        try
        {
            foreach (var file in Directory.GetFiles(GbAiPaths.RunDir, "config-*.yaml"))
            {
                var parts = Path.GetFileNameWithoutExtension(file).Split('-');
                if (parts.Length == 3 && int.TryParse(parts[1], out var pid) && pid != Environment.ProcessId && !IsAlive(pid)) TryDelete(file);
            }
        }
        catch (Exception)
        {
        }
    }

    // ---- Job Object: o gateway morre junto com o Revit ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobExtendedLimit info, int length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static void AssignToJob(Process process)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JobExtendedLimit { BasicLimitInformation = { LimitFlags = KillOnJobClose } };
                SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JobExtendedLimit>());
            }
            AssignProcessToJobObject(_job, process.Handle);
        }
        catch (Exception)
        {
            // Sem o Job Object o gateway ainda e encerrado por Stop(); so nao morre junto se o Revit for derrubado a forca.
        }
    }
}
