using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Gestaobim.Ai;
using Xunit;

namespace GBAi.Shared.Tests;

/// <summary>Um "gateway" falso em 127.0.0.1 que grava o que recebeu e responde com o que o teste mandar.</summary>
internal sealed class FakeGateway : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    public List<(string Method, string Path, string? Auth, string? GoogKey, string Body)> Requests { get; } = [];
    public Func<string, string, (int Status, string Body)> Responder { get; set; } = (_, _) => (200, "{}");
    public string BaseUrl { get; }

    public FakeGateway()
    {
        var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        BaseUrl = $"http://127.0.0.1:{number}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                var body = await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                lock (Requests)
                    Requests.Add((ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["Authorization"], ctx.Request.Headers["x-goog-api-key"], body));
                var (status, text) = Responder(ctx.Request.Url!.AbsolutePath, body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(text);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(1000); } catch (Exception) { }
    }
}

public class AiClientTests
{
    private const string Token = "tok-0123456789abcdef";

    [Fact]
    public async Task ListsModelsWithBearer()
    {
        using var fake = new FakeGateway { Responder = (_, _) => (200, """{"object":"list","data":[{"id":"a"},{"id":"gemini-x-image"}]}""") };
        using var client = new AiClient(fake.BaseUrl, Token);
        var models = await client.ListModelsAsync();
        Assert.Equal(["a", "gemini-x-image"], models);
        Assert.Equal($"Bearer {Token}", fake.Requests[0].Auth);
        Assert.Equal("/v1/models", fake.Requests[0].Path);
    }

    [Fact]
    public async Task ChatSendsSystemAndUserAndReturnsContent()
    {
        using var fake = new FakeGateway { Responder = (_, _) => (200, """{"choices":[{"message":{"content":"{\"chains\":[]}"}}]}""") };
        using var client = new AiClient(fake.BaseUrl, Token);
        var text = await client.ChatAsync("modelo-1", "regras", "cote isso");
        Assert.Equal("{\"chains\":[]}", text);
        var sent = JsonNode.Parse(fake.Requests[0].Body)!;
        Assert.Equal("modelo-1", sent["model"]!.GetValue<string>());
        Assert.Equal("system", sent["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("cote isso", sent["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal("/v1/chat/completions", fake.Requests[0].Path);
    }

    [Fact]
    public async Task GenerateImageSendsLabeledPartsAndParsesInlineData()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        using var fake = new FakeGateway
        {
            Responder = (_, _) => (200, """{"candidates":[{"content":{"parts":[{"text":"pronto"},{"inlineData":{"mimeType":"image/png","data":"PNGDATA"}}]}}]}""".Replace("PNGDATA", Convert.ToBase64String(png))),
        };
        using var client = new AiClient(fake.BaseUrl, Token);
        var result = await client.GenerateImageAsync("gemini-img", "faca o render", [new ImagePart("clay", "image/png", [1, 2, 3, 4])], "16:9", "2K");
        Assert.Equal(png, result.Bytes);
        Assert.Equal("pronto", result.Text);
        Assert.Equal("/v1beta/models/gemini-img:generateContent", fake.Requests[0].Path);
        Assert.Equal(Token, fake.Requests[0].GoogKey);
        var sent = JsonNode.Parse(fake.Requests[0].Body)!;
        var parts = sent["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("[clay]", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), parts[1]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Equal("faca o render", parts[^1]!["text"]!.GetValue<string>());
        Assert.Equal("16:9", sent["generationConfig"]!["imageConfig"]!["aspectRatio"]!.GetValue<string>());
    }

    [Fact]
    public void ParseImageReportsBlockReasonAndMissingImage()
    {
        var blocked = Assert.Throws<AiException>(() => AiClient.ParseImage(JsonNode.Parse("""{"promptFeedback":{"blockReason":"SAFETY"}}""")));
        Assert.Contains("SAFETY", blocked.Message);
        var none = Assert.Throws<AiException>(() => AiClient.ParseImage(JsonNode.Parse("""{"candidates":[{"content":{"parts":[{"text":"so texto"}]}}]}""")));
        Assert.Contains("so texto", none.Message);
        var snake = AiClient.ParseImage(JsonNode.Parse("""{"candidates":[{"content":{"parts":[{"inline_data":{"mime_type":"image/jpeg","data":"AQID"}}]}}]}"""));
        Assert.Equal("image/jpeg", snake.MimeType);
        Assert.Equal([1, 2, 3], snake.Bytes);
    }

    [Fact]
    public async Task HttpErrorsCarryStatusAndNeverEchoTheToken()
    {
        using var fake = new FakeGateway { Responder = (_, _) => (401, """{"error":{"message":"chave invalida TOKEN e Bearer abcdefghijklmnop99"}}""".Replace("TOKEN", Token)) };
        using var client = new AiClient(fake.BaseUrl, Token);
        var error = await Assert.ThrowsAsync<AiException>(() => client.ListModelsAsync());
        Assert.Equal(401, error.Status);
        Assert.DoesNotContain(Token, error.Message);
        Assert.DoesNotContain("abcdefghijklmnop99", error.Message);
    }

    [Fact]
    public async Task ConnectionRefusedBecomesAiException()
    {
        using var client = new AiClient("http://127.0.0.1:1", Token);
        await Assert.ThrowsAsync<AiException>(() => client.ListModelsAsync());
    }

    [Fact]
    public async Task EmptyChatAnswerIsAnError()
    {
        using var fake = new FakeGateway { Responder = (_, _) => (200, """{"choices":[{"message":{"content":""}}]}""") };
        using var client = new AiClient(fake.BaseUrl, Token);
        await Assert.ThrowsAsync<AiException>(() => client.ChatAsync("m", "s", "u"));
    }

    [Fact]
    public async Task OpenAiImageGenerationUsesJsonWhenNoInputImages()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2 };
        using var fake = new FakeGateway
        {
            Responder = (_, _) => (200, """{"data":[{"b64_json":"DATA"}]}""".Replace("DATA", Convert.ToBase64String(png))),
        };
        using var client = new AiClient(fake.BaseUrl, Token);
        var result = await client.GenerateImageAsync("gpt-image-1", "casa moderna", [], "16:9", "2K");
        Assert.Equal(png, result.Bytes);
        Assert.Equal("/v1/images/generations", fake.Requests[0].Path);
        var sent = JsonNode.Parse(fake.Requests[0].Body)!;
        Assert.Equal("gpt-image-1", sent["model"]!.GetValue<string>());
        Assert.Equal("1536x1024", sent["size"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAiInvalidBase64ThrowsAiException()
    {
        using var fake = new FakeGateway { Responder = (_, _) => (200, """{"data":[{"b64_json":"!!!invalido!!!"}]}""") };
        using var client = new AiClient(fake.BaseUrl, Token);
        await Assert.ThrowsAsync<AiException>(() => client.GenerateImageAsync("gpt-image-1", "teste", [], "1:1", null));
    }

    [Fact]
    public void PickModelRecognizesDallEAndImagenAsImageModels()
    {
        var models = new[] { "imagen-3.0-generate", "dall-e-3", "gpt-4o" };
        Assert.Equal("gpt-4o", AiSettings.PickModel(models, image: false));
        Assert.Equal("imagen-3.0-generate", AiSettings.PickModel(models, image: true));
        Assert.Equal("dall-e-3", AiSettings.PickModel(["dall-e-3"], image: true));
        Assert.Equal("", AiSettings.PickModel(["dall-e-3", "imagen-3.0"], image: false));
    }
}

[Collection("gbai-env")]
public class AiSettingsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gbai-test-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("GBAI_HOME");

    public AiSettingsTests() => Environment.SetEnvironmentVariable("GBAI_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GBAI_HOME", _previous);
        try { Directory.Delete(_home, true); } catch (Exception) { }
    }

    [Fact]
    public void KeysAreEncryptedOnDiskAndRoundTrip()
    {
        var settings = new AiSettings { PlanModel = "claude-x", ImageModel = "img", StripManufacturers = true };
        settings.Keys["gemini"] = "AIzaSy-SEGREDO-0123456789";
        settings.Save();

        var json = File.ReadAllText(Path.Combine(GbAiPaths.SettingsDir, "settings.json"));
        Assert.DoesNotContain("SEGREDO", json);
        var raw = File.ReadAllBytes(Path.Combine(GbAiPaths.SettingsDir, "keys.dat"));
        Assert.DoesNotContain("SEGREDO", Encoding.UTF8.GetString(raw));

        var loaded = AiSettings.Load();
        Assert.Equal("claude-x", loaded.PlanModel);
        Assert.True(loaded.StripManufacturers);
        Assert.True(loaded.HasKey("GEMINI"));
        Assert.Equal("AIzaSy-SEGREDO-0123456789", loaded.Keys["gemini"]);
        Assert.False(loaded.HasKey("claude"));
    }

    [Fact]
    public void CorruptFilesAreKeptBesideAndDefaultsReturn()
    {
        Directory.CreateDirectory(GbAiPaths.SettingsDir);
        File.WriteAllText(Path.Combine(GbAiPaths.SettingsDir, "settings.json"), "{ nao e json");
        File.WriteAllBytes(Path.Combine(GbAiPaths.SettingsDir, "keys.dat"), [1, 2, 3, 4]);
        var loaded = AiSettings.Load();
        Assert.Equal("", loaded.PlanModel);
        Assert.Empty(loaded.Keys);
        Assert.Equal(2, Directory.GetFiles(GbAiPaths.SettingsDir, "*.corrompido-*").Length);
    }

    [Fact]
    public void LoadWithNoFilesGivesDefaults()
    {
        var loaded = AiSettings.Load();
        Assert.False(loaded.OAuthRiskAcknowledged);
        Assert.Empty(loaded.ProviderKeys());
    }

    [Fact]
    public void DisconnectNeverLeavesTheAuthFolder()
    {
        GbAiPaths.EnsurePrivate(GbAiPaths.AuthDir);
        var inside = Path.Combine(GbAiPaths.AuthDir, "conta.json");
        var outside = Path.Combine(_home, "fora.json");
        File.WriteAllText(inside, "{}");
        File.WriteAllText(outside, "{}");
        OAuthLogin.Disconnect(@"..\..\fora.json");
        Assert.True(File.Exists(outside));
        OAuthLogin.Disconnect("conta");
        Assert.False(File.Exists(inside));
        Assert.Empty(OAuthLogin.ConnectedAccounts());
    }
}
