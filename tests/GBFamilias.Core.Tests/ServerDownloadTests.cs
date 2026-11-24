using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Gestaobim.Familias.Core;
using Gestaobim.Familias.Core.Server;

namespace GBFamilias.Core.Tests;

public sealed class ServerApiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gbfam-api").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly string Sha = new string('a', 64);

    private static ServerApi Api(FakeHandler handler, Func<CancellationToken, bool, Task<string>>? token = null)
    {
        var http = new HttpClient(handler);
        ServerApi.ConfigureClient(http, "9.9.9");
        return new ServerApi(http, "https://api.test/", token ?? ((_, _) => Task.FromResult("tk")));
    }

    private static HttpResponseMessage Bytes(HttpStatusCode status, byte[] data, Action<HttpResponseMessage>? tweak = null)
    {
        var r = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
        tweak?.Invoke(r);
        return r;
    }

    [Fact]
    public async Task EnviaTokenEUserAgentEPedeSoPeloSha()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Bytes(HttpStatusCode.OK, [1, 2, 3])));
        var part = Path.Combine(_dir, "a.part");
        var total = await Api(handler).DownloadFamilyAsync(Sha.ToUpperInvariant(), part, null, CancellationToken.None);
        var request = handler.Requests.Single();
        Assert.Equal("https://api.test/v1/rfa/" + Sha, request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("tk", request.Headers.Authorization.Parameter);
        Assert.Contains("GBFamilias/9.9.9", request.Headers.UserAgent.ToString());
        Assert.Equal(3, total);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(part));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("../../x")]
    public async Task RecusaShaInvalidoSemChamarARede(string sha)
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("nao deveria chamar"));
        await Assert.ThrowsAsync<ArgumentException>(() => Api(handler).DownloadFamilyAsync(sha, Path.Combine(_dir, "x"), null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Resposta401RenovaOTokenUmaVezESoEntaoDesiste()
    {
        var tokens = new List<bool>();
        var handler = new FakeHandler(r =>
            Task.FromResult(r.Headers.Authorization!.Parameter == "novo" ? Bytes(HttpStatusCode.OK, [7]) : Bytes(HttpStatusCode.Unauthorized, [])));
        var api = Api(handler, (_, force) => { tokens.Add(force); return Task.FromResult(force ? "novo" : "velho"); });
        Assert.Equal(1, await api.DownloadFamilyAsync(Sha, Path.Combine(_dir, "a"), null, CancellationToken.None));
        Assert.Equal([false, true], tokens);

        var sempre401 = new FakeHandler(_ => Task.FromResult(Bytes(HttpStatusCode.Unauthorized, [])));
        await Assert.ThrowsAsync<AuthRequiredException>(() => Api(sempre401).DownloadFamilyAsync(Sha, Path.Combine(_dir, "b"), null, CancellationToken.None));
        Assert.Equal(2, sempre401.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, typeof(NotAuthorizedException))]
    [InlineData(HttpStatusCode.NotFound, typeof(FamilyNotOnServerException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(ServerUnavailableException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(ServerUnavailableException))]
    public async Task StatusDeErroViraExcecaoEspecifica(HttpStatusCode status, Type expected)
    {
        var handler = new FakeHandler(_ => Task.FromResult(Bytes(status, [])));
        await Assert.ThrowsAsync(expected, () => Api(handler).DownloadFamilyAsync(Sha, Path.Combine(_dir, "a"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Resposta429TrazORetryAfterEOMotivo()
    {
        var handler = new FakeHandler(_ => Task.FromResult(FakeHandler.Json((HttpStatusCode)429, "{\"error\":\"limite de 1 download simultaneo\"}")
            .Also(r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)))));
        var error = await Assert.ThrowsAsync<RateLimitedException>(() => Api(handler).DownloadFamilyAsync(Sha, Path.Combine(_dir, "a"), null, CancellationToken.None));
        Assert.Equal(7, error.RetryAfterSeconds);
        Assert.Contains("simultaneo", error.Message);
    }

    [Fact]
    public async Task SemRedeViraServerUnavailable()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("dns"));
        await Assert.ThrowsAsync<ServerUnavailableException>(() => Api(handler).DownloadFamilyAsync(Sha, Path.Combine(_dir, "a"), null, CancellationToken.None));
        await Assert.ThrowsAsync<ServerUnavailableException>(() => Api(handler).GetCatalogFileAsync("manifest.json", CancellationToken.None));
    }

    [Fact]
    public async Task RetomaPorRangeAcrescentandoAoParcial()
    {
        var part = Path.Combine(_dir, "a.part");
        File.WriteAllBytes(part, [1, 2, 3, 4]);
        var handler = new FakeHandler(r =>
        {
            Assert.Equal(4, r.Headers.Range!.Ranges.Single().From);
            return Task.FromResult(Bytes(HttpStatusCode.PartialContent, [5, 6]));
        });
        var total = await Api(handler).DownloadFamilyAsync(Sha, part, null, CancellationToken.None);
        Assert.Equal(6, total);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, File.ReadAllBytes(part));
    }

    [Fact]
    public async Task Resposta416DescartaParcialERecomecaDoZero()
    {
        var part = Path.Combine(_dir, "stale.part");
        File.WriteAllBytes(part, [1, 2, 3, 4, 5, 6, 7, 8]);
        var calls = 0;
        var handler = new FakeHandler(r =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal(8, r.Headers.Range!.Ranges.Single().From);
                return Task.FromResult(Bytes(HttpStatusCode.RequestedRangeNotSatisfiable, []));
            }
            Assert.Null(r.Headers.Range);
            return Task.FromResult(Bytes(HttpStatusCode.OK, [42, 43, 44]));
        });
        var total = await Api(handler).DownloadFamilyAsync(Sha, part, null, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal(3, total);
        Assert.Equal(new byte[] { 42, 43, 44 }, File.ReadAllBytes(part));
    }

    [Fact]
    public async Task ServidorQueIgnoraRangeRecomecaDoZero()
    {
        var part = Path.Combine(_dir, "a.part");
        File.WriteAllBytes(part, [9, 9, 9]);
        var handler = new FakeHandler(_ => Task.FromResult(Bytes(HttpStatusCode.OK, [1, 2, 3, 4])));
        Assert.Equal(4, await Api(handler).DownloadFamilyAsync(Sha, part, null, CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(part));
    }

    [Fact]
    public async Task ReportaProgressoECancelamentoInterrompe()
    {
        var data = new byte[300_000];
        var handler = new FakeHandler(_ => Task.FromResult(Bytes(HttpStatusCode.OK, data)));
        var seen = new List<(long, long)>();
        await Api(handler).DownloadFamilyAsync(Sha, Path.Combine(_dir, "a"), new SyncProgress<(long, long)>(seen.Add), CancellationToken.None);
        Assert.Equal((300_000L, 300_000L), seen.Last());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Api(handler).DownloadFamilyAsync(Sha, Path.Combine(_dir, "b"), null, cts.Token));
    }
}

internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal static class HttpExtensions
{
    public static HttpResponseMessage Also(this HttpResponseMessage r, Action<HttpResponseMessage> tweak)
    {
        tweak(r);
        return r;
    }
}

public sealed class DownloadManagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gbfam-dl").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private static string ShaOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static FamilyRecord Record(byte[] content, string rel = "#PORTAS\\Porta 1.rfa") => new()
    {
        Id = 1, Sha = ShaOf(content), Name = "Porta 1", RelPath = rel, SizeBytes = content.Length,
    };

    private static (DownloadManager manager, FakeHandler handler) Make(Func<HttpRequestMessage, HttpResponseMessage> serve, string machine = "PC-1")
    {
        var handler = new FakeHandler(r => Task.FromResult(serve(r)));
        var http = new HttpClient(handler);
        return (new DownloadManager(new ServerApi(http, "https://api.test", (_, _) => Task.FromResult("tk")), machine), handler);
    }

    private static HttpResponseMessage Ok(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    [Fact]
    public async Task BaixaConfereOHashEPublicaNoCaminhoDaBiblioteca()
    {
        var content = "familia"u8.ToArray();
        var (manager, handler) = Make(_ => Ok(content));
        var result = await manager.EnsureAsync(Record(content), _root, null, CancellationToken.None);
        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal(Path.Combine(_root, "#PORTAS", "Porta 1.rfa"), result.Path);
        Assert.Equal(content, File.ReadAllBytes(result.Path));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "#PORTAS"), "*.part"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ArquivoJaPresenteEValidoNaoBaixaDeNovo()
    {
        var content = "familia"u8.ToArray();
        var (manager, handler) = Make(_ => Ok(content));
        await manager.EnsureAsync(Record(content), _root, null, CancellationToken.None);
        var second = await manager.EnsureAsync(Record(content), _root, null, CancellationToken.None);
        Assert.Equal(DownloadStatus.AlreadyPresent, second.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ArquivoLocalCorrompidoEBaixadoDeNovo()
    {
        var content = "familia"u8.ToArray();
        var (manager, handler) = Make(_ => Ok(content));
        var path = Path.Combine(_root, "#PORTAS", "Porta 1.rfa");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "lixo");
        var result = await manager.EnsureAsync(Record(content), _root, null, CancellationToken.None);
        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task HashDivergenteTentaDeNovoEDepoisFalhaSemDeixarLixo()
    {
        var content = "familia"u8.ToArray();
        var (manager, handler) = Make(_ => Ok("outra coisa"u8.ToArray()));
        await Assert.ThrowsAsync<IntegrityException>(() => manager.EnsureAsync(Record(content), _root, null, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
        var dir = Path.Combine(_root, "#PORTAS");
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task PrimeiraRespostaCorrompidaMasSegundaBoaFunciona()
    {
        var content = "familia"u8.ToArray();
        var calls = 0;
        var (manager, _) = Make(_ => Ok(++calls == 1 ? "corrompido"u8.ToArray() : content));
        var result = await manager.EnsureAsync(Record(content), _root, null, CancellationToken.None);
        Assert.Equal(content, File.ReadAllBytes(result.Path));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EsperaORetryAfterEContinuaQuando429()
    {
        var content = "familia"u8.ToArray();
        var calls = 0;
        var (manager, handler) = Make(_ =>
        {
            if (++calls == 1)
            {
                var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":\"limite\"}") };
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                return r;
            }
            return Ok(content);
        });
        var phases = new List<DownloadPhase>();
        var result = await manager.EnsureAsync(Record(content), _root, new SyncProgress<DownloadProgress>(p => phases.Add(p.Phase)), CancellationToken.None);
        Assert.Equal(DownloadStatus.Downloaded, result.Status);
        Assert.Contains(DownloadPhase.WaitingForSlot, phases);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CaminhoPerigosoNoCatalogoERecusado()
    {
        var content = "x"u8.ToArray();
        var (manager, handler) = Make(_ => Ok(content));
        foreach (var rel in new[] { "..\\fora.rfa", "C:\\x.rfa", "/etc/x.rfa" })
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureAsync(Record(content, rel), _root, null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DoisPedidosSimultaneosSaoServidosUmDeCadaVez()
    {
        var a = "aaa"u8.ToArray();
        var b = "bbb"u8.ToArray();
        var active = 0;
        var maxActive = 0;
        var handler = new FakeHandler(async r =>
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            await Task.Delay(100);
            Interlocked.Decrement(ref active);
            return Ok(r.RequestUri!.AbsolutePath.EndsWith(ShaOf(a)) ? a : b);
        });
        var manager = new DownloadManager(new ServerApi(new HttpClient(handler), "https://api.test", (_, _) => Task.FromResult("tk")), "PC");
        await Task.WhenAll(
            manager.EnsureAsync(Record(a, "x\\a.rfa"), _root, null, CancellationToken.None),
            manager.EnsureAsync(Record(b, "x\\b.rfa"), _root, null, CancellationToken.None));
        Assert.Equal(1, maxActive);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CancelarNaFilaNaoBaixaNada()
    {
        var content = "x"u8.ToArray();
        var gate = new TaskCompletionSource();
        var handler = new FakeHandler(async _ => { await gate.Task; return Ok(content); });
        var manager = new DownloadManager(new ServerApi(new HttpClient(handler), "https://api.test", (_, _) => Task.FromResult("tk")), "PC");
        var first = manager.EnsureAsync(Record(content, "x\\a.rfa"), _root, null, CancellationToken.None);
        await Task.Delay(100);
        using var cts = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.EnsureAsync(Record(content, "x\\b.rfa"), _root, null, cts.Token));
        gate.SetResult();
        await first;
        Assert.Single(handler.Requests);
        Assert.Equal(0, manager.Waiting);
    }

    [Fact]
    public async Task OutraMaquinaPublicouPrimeiroUsaOArquivoDela()
    {
        var content = "familia"u8.ToArray();
        var final = Path.Combine(_root, "#PORTAS", "Porta 1.rfa");
        var (manager, _) = Make(_ =>
        {
            // enquanto "baixamos", outro computador da rede termina e publica o mesmo arquivo
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            File.WriteAllBytes(final, content);
            return Ok(content);
        });
        var result = await manager.EnsureAsync(Record(content), _root, null, CancellationToken.None);
        Assert.Equal(content, File.ReadAllBytes(result.Path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(final)!, "*.part"));
    }

    [Fact]
    public async Task ParcialDeOutraMaquinaNaoEMexido()
    {
        var content = "familia"u8.ToArray();
        var dir = Path.Combine(_root, "#PORTAS");
        Directory.CreateDirectory(dir);
        var theirs = Path.Combine(dir, "Porta 1.rfa.OUTROPC.part");
        File.WriteAllText(theirs, "parcial do outro computador");
        var (manager, _) = Make(_ => Ok(content), "PC-1");
        await manager.EnsureAsync(Record(content), _root, null, CancellationToken.None);
        Assert.True(File.Exists(theirs));
    }
}

public sealed class DownloadFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gbfam-folder").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PastaVaziaEObrigatoria(string? path) => Assert.Contains("Escolha", DownloadFolder.Validate(path));

    [Fact]
    public void CaminhoRelativoELongoDemaisSaoRecusados()
    {
        Assert.Contains("completo", DownloadFolder.Validate("familias"));
        Assert.Contains("longo", DownloadFolder.Validate(Path.Combine(_root, new string('a', 200))));
    }

    [Fact]
    public void PastaValidaEhCriadaEAceita()
    {
        var path = Path.Combine(_root, "nova", "pasta");
        Assert.Null(DownloadFolder.Validate(path));
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(path)); // o arquivo de teste de escrita foi apagado
    }

    [Fact]
    public void PastaSobreUmArquivoNaoServe()
    {
        var file = Path.Combine(_root, "arquivo.txt");
        File.WriteAllText(file, "x");
        Assert.NotNull(DownloadFolder.Validate(Path.Combine(file, "sub")));
    }

    [Theory]
    [InlineData(@"\\servidor\pasta", true)]
    [InlineData("//servidor/pasta", true)]
    [InlineData(@"C:\Familias", false)]
    public void ReconhecePastaDeRede(string path, bool network) => Assert.Equal(network, DownloadFolder.IsNetworkPath(path));
}

public sealed class CacheEvictorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gbfam-cache").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string File_(string name, int bytes, int ageHours)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        var when = DateTime.UtcNow.AddHours(-ageHours);
        File.SetLastWriteTimeUtc(path, when);
        File.SetLastAccessTimeUtc(path, when);
        return path;
    }

    [Fact]
    public void ApagaOsMaisAntigosAteCabernoLimite()
    {
        var old = File_("a\\velho.rfa", 400, 48);
        var mid = File_("a\\medio.rfa", 400, 24);
        var recent = File_("b\\novo.rfa", 400, 2);
        var freed = CacheEvictor.Evict(_root, 900);
        Assert.Equal(400, freed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(mid));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void NaoApagaProtegidosNemRecemBaixadosNemOQueNaoERfa()
    {
        var protectedFile = File_("p.rfa", 500, 100);
        var fresh = File_("f.rfa", 500, 0);
        var other = File_("notas.txt", 5000, 100);
        Assert.Equal(0, CacheEvictor.Evict(_root, 10, [protectedFile]));
        Assert.True(File.Exists(protectedFile) && File.Exists(fresh) && File.Exists(other));
    }

    [Fact]
    public void SemLimiteOuPastaDeRedeNaoFazNada()
    {
        var f = File_("a.rfa", 500, 100);
        Assert.Equal(0, CacheEvictor.Evict(_root, 0));
        Assert.Equal(0, CacheEvictor.Evict(_root, -1));
        Assert.Equal(0, CacheEvictor.Evict(@"\\servidor\pasta", 1));
        Assert.True(File.Exists(f));
        Assert.Equal(0, CacheEvictor.Evict(Path.Combine(_root, "nao-existe"), 1));
    }
}
