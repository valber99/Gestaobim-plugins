using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Gestaobim.Familias.Core.Server;

namespace GBFamilias.Core.Tests;

public sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        lock (Requests) Requests.Add(request);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel);
        lock (Bodies) Bodies.Add(body);
        return await respond(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class Ed25519Tests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    // Vetores de teste da RFC 8032, secao 7.1
    [Theory]
    [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    [InlineData("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
        "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
        "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
    public void VetoresDaRfc8032(string seed, string pub, string msg, string sig)
    {
        var s = H(seed);
        var derived = Ed25519.PublicKeyFromSeed(s);
        Assert.Equal(pub, Convert.ToHexString(derived).ToLowerInvariant());
        Assert.Equal(sig, Convert.ToHexString(Ed25519.Sign(s, H(msg))).ToLowerInvariant());
        Assert.True(Ed25519.Verify(derived, H(msg), H(sig)));
    }

    [Fact]
    public void AssinarEConferirDaOMesmoResultadoParaMensagensVariadas()
    {
        var seed = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1)).ToArray();
        var pub = Ed25519.PublicKeyFromSeed(seed);
        foreach (var size in new[] { 0, 1, 31, 32, 33, 64, 1000, 100_000 })
        {
            var msg = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
            Assert.True(Ed25519.Verify(pub, msg, Ed25519.Sign(seed, msg)), $"tamanho {size}");
        }
    }

    [Fact]
    public void MensagemAdulteradaOuChaveErradaFalham()
    {
        var seed = new byte[32]; seed[0] = 9;
        var other = new byte[32]; other[0] = 10;
        var pub = Ed25519.PublicKeyFromSeed(seed);
        var msg = Encoding.UTF8.GetBytes("{\"version\":1}");
        var sig = Ed25519.Sign(seed, msg);
        Assert.True(Ed25519.Verify(pub, msg, sig));
        Assert.False(Ed25519.Verify(pub, Encoding.UTF8.GetBytes("{\"version\":2}"), sig));
        Assert.False(Ed25519.Verify(Ed25519.PublicKeyFromSeed(other), msg, sig));
        var flipped = (byte[])sig.Clone(); flipped[10] ^= 1;
        Assert.False(Ed25519.Verify(pub, msg, flipped));
        var flippedS = (byte[])sig.Clone(); flippedS[40] ^= 1;
        Assert.False(Ed25519.Verify(pub, msg, flippedS));
    }

    [Fact]
    public void EntradasMalformadasNuncaLancamEViramFalso()
    {
        var pub = Ed25519.PublicKeyFromSeed(new byte[32]);
        var msg = new byte[] { 1, 2, 3 };
        Assert.False(Ed25519.Verify([], msg, new byte[64]));
        Assert.False(Ed25519.Verify(pub, msg, []));
        Assert.False(Ed25519.Verify(pub, msg, new byte[63]));
        Assert.False(Ed25519.Verify(pub, msg, new byte[64]));
        Assert.False(Ed25519.Verify(Enumerable.Repeat((byte)0xFF, 32).ToArray(), msg, new byte[64]));
        // S maior ou igual a L e recusado (maleabilidade)
        var sig = Ed25519.Sign(new byte[32], msg);
        var big = (byte[])sig.Clone();
        for (var i = 32; i < 64; i++) big[i] = 0xFF;
        Assert.False(Ed25519.Verify(Ed25519.PublicKeyFromSeed(new byte[32]), msg, big));
    }

    [Fact]
    public void AssinaturaGeradaPeloRustConferePeloCSharp()
    {
        // Se o catálogo publicado estiver disponível via variável de ambiente, valida assinatura real gerada pelo Rust
        var dir = Environment.GetEnvironmentVariable("GBFAM_CATALOG_TEST_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return;
        var pubFile = Path.Combine(dir, "catalog.pub");
        var manifest = Path.Combine(dir, "catalog-out", "manifest.json");
        var sigFile = Path.Combine(dir, "catalog-out", "manifest.sig");
        if (!File.Exists(pubFile) || !File.Exists(manifest) || !File.Exists(sigFile)) return;

        var pub = File.ReadAllText(pubFile).Trim();
        var data = File.ReadAllBytes(manifest);
        var sig = File.ReadAllText(sigFile).Trim();
        Assert.True(CatalogCrypto.VerifyManifest(data, sig, pub), "o C# nao confere a assinatura feita pelo Rust");
        data[5] ^= 1;
        Assert.False(CatalogCrypto.VerifyManifest(data, sig, pub));
    }

    [Fact]
    public void VerifyManifestComBase64RuimRetornaFalso()
    {
        Assert.False(CatalogCrypto.VerifyManifest([1], "nao e base64!!", "QjzbuV+qjwLBP9ZFk1bvwbScfIrSISNjM7NjgMpQf7E="));
        Assert.False(CatalogCrypto.VerifyManifest([1], "AAAA", "chave ruim"));
    }
}

public class PkceTests
{
    [Fact]
    public void DesafioDoExemploDaRfc7636()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void VerificadorTemTamanhoECaracteresValidosEMudaACadaVez()
    {
        var a = Pkce.NewVerifier();
        var b = Pkce.NewVerifier();
        Assert.NotEqual(a, b);
        Assert.InRange(a.Length, 43, 128);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~'));
        Assert.DoesNotContain('=', Pkce.Challenge(a));
        Assert.NotEqual(Pkce.NewState(), Pkce.NewState());
    }
}

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gbfam-tok").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Reverse : ITokenProtector
    {
        public byte[] Protect(byte[] plain) => plain.Reverse().ToArray();
        public byte[] Unprotect(byte[] protectedData) => protectedData.Reverse().ToArray();
    }

    private sealed class Throwing : ITokenProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] protectedData) => throw new System.Security.Cryptography.CryptographicException("outro usuario");
    }

    [Fact]
    public void GravaELeSemGuardarTextoClaroEmDisco()
    {
        var path = Path.Combine(_dir, "s.bin");
        var store = new TokenStore(path, new Reverse());
        var tokens = new TokenSet("acesso-secreto", "renovacao-secreta", new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        store.Save(tokens);
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("acesso-secreto", raw);
        Assert.Equal(tokens, store.Load());
    }

    [Fact]
    public void ArquivoAusenteCorrompidoOuDeOutroUsuarioViraNull()
    {
        var path = Path.Combine(_dir, "s.bin");
        Assert.Null(new TokenStore(path, new NoProtection()).Load());
        File.WriteAllText(path, "isto nao e json");
        Assert.Null(new TokenStore(path, new NoProtection()).Load());
        File.WriteAllText(path, "{\"x\":1}");
        Assert.Null(new TokenStore(path, new NoProtection()).Load());
        new TokenStore(path, new NoProtection()).Save(new TokenSet("a", null, DateTime.UtcNow));
        Assert.Null(new TokenStore(path, new Throwing()).Load());
    }

    [Fact]
    public void ClearApagaEToleraArquivoInexistente()
    {
        var path = Path.Combine(_dir, "s.bin");
        var store = new TokenStore(path, new NoProtection());
        store.Clear();
        store.Save(new TokenSet("a", "b", DateTime.UtcNow));
        store.Clear();
        Assert.False(File.Exists(path));
    }
}

public sealed class AuthClientTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gbfam-auth").FullName;
    private DateTime _now = new(2030, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    public void Dispose() => Directory.Delete(_dir, true);

    private static ServerConfig Config(params int[] ports) => new() { Issuer = "https://auth.test", ClientId = "cid", ProjectId = "proj", RedirectPorts = ports.Length > 0 ? ports : [53682] };

    private (AuthClient client, FakeHandler handler, TokenStore store) Make(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> respond, ServerConfig? cfg = null)
    {
        FakeHandler? handler = null;
        handler = new FakeHandler(async r => await respond(r, handler!.Bodies.Last()));
        var store = new TokenStore(Path.Combine(_dir, "t.bin"), new NoProtection());
        return (new AuthClient(cfg ?? Config(), new HttpClient(handler), store, () => _now), handler, store);
    }

    private static Dictionary<string, string> Form(string body) =>
        body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private static string TokenJson(string access, string? refresh = "r1", int expires = 300) =>
        new JsonObject { ["access_token"] = access, ["refresh_token"] = refresh, ["expires_in"] = expires }.ToJsonString();

    [Fact]
    public void UrlDeAutorizacaoTemPkceS256EOEscopoComPapeisEAudiencia()
    {
        var (client, _, _) = Make((_, _) => throw new InvalidOperationException());
        var url = client.BuildAuthorizeUrl("http://127.0.0.1:53682/callback", "est", "desafio");
        Assert.StartsWith("https://auth.test/oauth/v2/authorize?", url);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal("cid", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("desafio", q["code_challenge"]);
        Assert.Equal("est", q["state"]);
        Assert.Equal("http://127.0.0.1:53682/callback", q["redirect_uri"]);
        var scope = q["scope"]!.Split(' ');
        Assert.Contains("offline_access", scope);
        Assert.Contains("urn:zitadel:iam:org:project:id:proj:aud", scope);
        Assert.Contains("urn:zitadel:iam:org:projects:roles", scope);
    }

    [Fact]
    public async Task LoginCompletoTrocaOCodigoEGuardaOsTokens()
    {
        string? authorizeUrl = null;
        var (client, handler, store) = Make((_, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, TokenJson("acesso-1", "ref-1"))), Config(53690, 53691));

        await client.LoginAsync(url =>
        {
            authorizeUrl = url;
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            // "navegador": volta para o redirect com o codigo e o state
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetAsync($"{q["redirect_uri"]}?code=CODIGO123&state={Uri.EscapeDataString(q["state"]!)}");
            });
        }, CancellationToken.None, TimeSpan.FromSeconds(10));

        Assert.True(client.HasSession);
        Assert.Equal("acesso-1", await client.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("acesso-1", store.Load()!.AccessToken);

        var form = Form(handler.Bodies.Single());
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("CODIGO123", form["code"]);
        Assert.Equal("cid", form["client_id"]);
        Assert.StartsWith("http://127.0.0.1:5369", form["redirect_uri"]);
        // o verificador enviado na troca e o que gerou o desafio da URL
        var challenge = System.Web.HttpUtility.ParseQueryString(new Uri(authorizeUrl!).Query)["code_challenge"];
        Assert.Equal(challenge, Pkce.Challenge(form["code_verifier"]));
        Assert.DoesNotContain(handler.Bodies, b => b.Contains("client_secret"));
    }

    [Fact]
    public async Task StateDiferenteEErroDoServidorSaoRecusados()
    {
        var (client, handler, _) = Make((_, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, TokenJson("x"))), Config(53692));
        var forged = await Assert.ThrowsAsync<AuthRequiredException>(() => client.LoginAsync(url =>
        {
            var redirect = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["redirect_uri"];
            _ = Task.Run(async () => { using var h = new HttpClient(); await h.GetAsync($"{redirect}?code=C&state=forjado"); });
        }, CancellationToken.None, TimeSpan.FromSeconds(10)));
        Assert.Contains("state", forged.Message);
        Assert.Empty(handler.Requests); // nao chegou a trocar o codigo
        Assert.False(client.HasSession);

        var denied = await Assert.ThrowsAsync<AuthRequiredException>(() => client.LoginAsync(url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
            _ = Task.Run(async () => { using var h = new HttpClient(); await h.GetAsync($"{q["redirect_uri"]}?error=access_denied&state={q["state"]}"); });
        }, CancellationToken.None, TimeSpan.FromSeconds(10)));
        Assert.Contains("access_denied", denied.Message);
    }

    [Fact]
    public async Task LoginSemRetornoDoNavegadorExpira()
    {
        var (client, _, _) = Make((_, _) => throw new InvalidOperationException(), Config(53693));
        await Assert.ThrowsAsync<AuthRequiredException>(() => client.LoginAsync(_ => { }, CancellationToken.None, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task UsaAProximaPortaSeAPrimeiraEstaOcupada()
    {
        using var busy = LoopbackListener.Start([53694]);
        using var next = LoopbackListener.Start([53694, 53695]);
        Assert.Equal(53695, next.Port);
        Assert.Throws<IOException>(() => LoopbackListener.Start([53694, 53695]));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ListenerIgnoraFaviconEAtendeSoOCallback()
    {
        using var listener = LoopbackListener.Start([53696]);
        var wait = listener.WaitAsync(CancellationToken.None);
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("http://127.0.0.1:53696/favicon.ico")).StatusCode);
        Assert.False(wait.IsCompleted);
        var page = await http.GetStringAsync("http://127.0.0.1:53696/callback?code=a%20b&state=s");
        Assert.Contains("Login conclu", page);
        Assert.DoesNotContain("<script", page);
        var result = await wait;
        Assert.Equal(("a b", "s"), (result.Code, result.State));
    }

    [Fact]
    public async Task TokenFrescoNaoRenovaETokenVencidoRenovaUmaVezSo()
    {
        var calls = 0;
        var (client, handler, store) = Make(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50);
            return FakeHandler.Json(HttpStatusCode.OK, TokenJson("novo", null, 300));
        });
        store.Save(new TokenSet("velho", "ref", _now.AddSeconds(20))); // vence em 20s: dentro da margem de 60s
        client = new AuthClient(Config(), new HttpClient(handler), store, () => _now);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAccessTokenAsync(CancellationToken.None)));
        Assert.All(tokens, t => Assert.Equal("novo", t));
        Assert.Equal(1, calls);
        var form = Form(handler.Bodies.Single());
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("ref", form["refresh_token"]);
        // o refresh token antigo e mantido quando o servidor nao manda outro
        Assert.Equal("ref", store.Load()!.RefreshToken);

        Assert.Equal("novo", await client.GetAccessTokenAsync(CancellationToken.None)); // fresco: nao chama de novo
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RenovacaoRecusadaLimpaASessaoEPedeLogin()
    {
        var (client, handler, store) = Make((_, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")));
        store.Save(new TokenSet("velho", "ref", _now.AddMinutes(-5)));
        client = new AuthClient(Config(), new HttpClient(handler), store, () => _now);
        var error = await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetAccessTokenAsync(CancellationToken.None));
        Assert.Contains("expirou", error.Message);
        Assert.False(client.HasSession);
        Assert.Null(store.Load());
        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SemInternetNaoApagaASessao()
    {
        var (client, handler, store) = Make((_, _) => throw new HttpRequestException("sem rede"));
        store.Save(new TokenSet("velho", "ref", _now.AddMinutes(-5)));
        client = new AuthClient(Config(), new HttpClient(handler), store, () => _now);
        await Assert.ThrowsAsync<ServerUnavailableException>(() => client.GetAccessTokenAsync(CancellationToken.None));
        Assert.NotNull(store.Load()); // a sessao continua valida quando a internet voltar
    }

    [Fact]
    public async Task SemTokenDeRenovacaoPedeLoginEForcarRenovacaoFunciona()
    {
        var (client, handler, store) = Make((_, _) => Task.FromResult(FakeHandler.Json(HttpStatusCode.OK, TokenJson("forcado"))));
        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetAccessTokenAsync(CancellationToken.None));
        store.Save(new TokenSet("valido", "ref", _now.AddHours(1)));
        client = new AuthClient(Config(), new HttpClient(handler), store, () => _now);
        Assert.Equal("valido", await client.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("forcado", await client.GetAccessTokenAsync(CancellationToken.None, forceRefresh: true));
        client.Logout();
        Assert.False(client.HasSession);
    }
}
