using System.Text.Json.Nodes;
using Gestaobim.Ai;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GBAi.Shared.Tests;

public class GatewayConfigTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string Mgmt = "fedcba9876543210fedcba9876543210";

    private static string Build(IEnumerable<ProviderKey>? keys = null) =>
        GatewayConfig.Build(18317, Token, Mgmt, @"C:\Users\x\AppData\Local\GestaoBIM\gbai\auth", keys);

    [Fact]
    public void GeneratedConfigPassesVerification()
    {
        var yaml = Build();
        Assert.Empty(GatewayConfig.Verify(yaml, 18317, Token, @"C:\Users\x\AppData\Local\GestaoBIM\gbai\auth"));
    }

    [Fact]
    public void ConfigBindsOnlyLoopbackAndLimitsBody()
    {
        var map = MiniYaml.Flatten(Build());
        Assert.Equal("127.0.0.1", map["server.host"]);
        Assert.Equal("67108864", map["server.max-request-body-bytes"]);
        Assert.Equal("false", map["management.allow-remote"]);
        Assert.Equal("true", map["management.disable-control-panel"]);
        Assert.Equal("false", map["observability.logs.request-log"]);
        Assert.Equal("false", map["server.discovery.enabled"]);
    }

    [Theory]
    [InlineData("  host: \"127.0.0.1\"", "  host: \"\"")]                       // todas as interfaces: o padrao do projeto
    [InlineData("  host: \"127.0.0.1\"", "  host: \"0.0.0.0\"")]
    [InlineData("  max-request-body-bytes: 67108864", "  max-request-body-bytes: 0")]
    [InlineData("    enabled: false\n  kv-store", "    enabled: true\n  kv-store")]  // mDNS
    [InlineData("  allow-remote: false", "  allow-remote: true")]
    [InlineData("  disable-control-panel: true", "  disable-control-panel: false")]
    [InlineData("    request-log: false", "    request-log: true")]
    [InlineData("plugins:\n  enabled: false", "plugins:\n  enabled: true")]
    public void AnyDivergenceIsRefused(string find, string replace)
    {
        var yaml = Build();
        Assert.Contains(find.Replace("\n", Environment.NewLine), yaml.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
        var tampered = yaml.Replace("\r\n", "\n").Replace(find, replace);
        Assert.NotEmpty(GatewayConfig.Verify(tampered, 18317, Token, @"C:\Users\x\AppData\Local\GestaoBIM\gbai\auth"));
    }

    [Fact]
    public void SecondClientTokenAndWrongPortAreRefused()
    {
        var yaml = Build().Replace($"    - \"{Token}\"", $"    - \"{Token}\"\n    - \"outro-token-qualquer-123\"");
        Assert.Contains(GatewayConfig.Verify(yaml, 18317, Token, @"C:\Users\x\AppData\Local\GestaoBIM\gbai\auth"), p => p.StartsWith("access.api-keys"));
        Assert.Contains(GatewayConfig.Verify(Build(), 9999, Token, @"C:\Users\x\AppData\Local\GestaoBIM\gbai\auth"), p => p.StartsWith("server.port"));
    }

    [Fact]
    public void ShortSecretsAreRejectedAtBuild()
    {
        Assert.Throws<ArgumentException>(() => GatewayConfig.Build(1234, "curto", Mgmt, "C:/a"));
        Assert.Throws<ArgumentException>(() => GatewayConfig.Build(1234, Token, "curta", "C:/a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => GatewayConfig.Build(0, Token, Mgmt, "C:/a"));
    }

    [Fact]
    public void ProviderKeysGoUnderApiKeysAndKeepVerificationClean()
    {
        var keys = new[] { new ProviderKey("gemini", "AIzaSyEXAMPLEKEY000000000000000000000"), new ProviderKey("claude", "sk-ant-EXAMPLE0000000000000000") };
        var yaml = Build(keys);
        var map = MiniYaml.Flatten(yaml);
        Assert.Contains(map, p => p.Value == "AIzaSyEXAMPLEKEY000000000000000000000");
        Assert.Contains("gemini:", yaml);
        Assert.Contains("claude:", yaml);
        Assert.Empty(GatewayConfig.Verify(yaml, 18317, Token, @"C:\Users\x\AppData\Local\GestaoBIM\gbai\auth"));
    }

    [Fact]
    public void UnknownProvidersAndBlankKeysAreDropped()
    {
        var yaml = Build([new ProviderKey("desconhecido", "abc"), new ProviderKey("gemini", "  ")]);
        Assert.DoesNotContain("api-keys:\n  ", yaml.Replace("\r\n", "\n").Replace("access:\n  api-keys:", ""));
        Assert.DoesNotContain("desconhecido", yaml);
    }

    [Fact]
    public void QuotesAndBackslashesInPathsAreEscaped()
    {
        var yaml = GatewayConfig.Build(1234, Token, Mgmt, "C:\\Users\\a \"b\"\\auth");
        Assert.Equal("C:/Users/a \"b\"/auth", MiniYaml.Flatten(yaml)["oauth.auth-dir"]);
    }
}

public class MiniYamlTests
{
    [Fact]
    public void ReadsNestedMapsListsAndIgnoresComments()
    {
        var map = MiniYaml.Flatten("""
            # comentario
            a:
              b: 1   # depois
              c: "x # nao e comentario"
              lista:
                - um
                - "dois"
            d: ok
            """);
        Assert.Equal("1", map["a.b"]);
        Assert.Equal("x # nao e comentario", map["a.c"]);
        Assert.Equal("um", map["a.lista[0]"]);
        Assert.Equal("dois", map["a.lista[1]"]);
        Assert.Equal("ok", map["d"]);
    }

    [Fact]
    public void ReadsListOfMaps()
    {
        var map = MiniYaml.Flatten("""
            api-keys:
              gemini:
                - name: "g"
                  keys:
                    - api-key: "K1"
            """);
        Assert.Contains(map, p => p.Value == "K1");
        Assert.Contains(map, p => p.Key.EndsWith(".name") && p.Value == "g");
    }
}

public class RedactorTests
{
    [Fact]
    public void RemovesGivenSecretsKnownKeyShapesAndBase64()
    {
        var text = "token=abcdef123456 chave sk-ant-api03-ABCDEFGHIJKLMNOP1234 e AIzaSyABCDEFGHIJKLMNOPQRSTUVWX e " + new string('A', 400);
        var clean = Redactor.Scrub(text, "abcdef123456");
        Assert.DoesNotContain("abcdef123456", clean);
        Assert.DoesNotContain("sk-ant", clean);
        Assert.DoesNotContain("AIzaSy", clean);
        Assert.DoesNotContain(new string('A', 200), clean);
        Assert.Contains("[dados 400 caracteres]", clean);
    }

    [Fact]
    public void DescribeRequestReplacesImagesAndNeverEchoesTokens()
    {
        var body = new JsonObject
        {
            ["model"] = "m",
            ["parts"] = new JsonArray(new JsonObject { ["inlineData"] = new JsonObject { ["data"] = Convert.ToBase64String(new byte[3000]) } }),
            ["header"] = "Bearer abcdefghijklmnop1234",
        };
        var text = Redactor.DescribeRequest(body);
        Assert.Contains("[imagem 3000 bytes]", text);
        Assert.DoesNotContain("abcdefghijklmnop1234", text);
        Assert.Equal(Convert.ToBase64String(new byte[3000]), body["parts"]![0]!["inlineData"]!["data"]!.GetValue<string>());   // o original nao foi alterado
    }
}

public class JsonExtractTests
{
    [Theory]
    [InlineData("Segue o plano:\n```json\n{\"a\": {\"b\": [1,2]}}\n```\nfim", "{\"a\": {\"b\": [1,2]}}")]
    [InlineData("{\"a\":\"}\"} e mais {\"x\":1}", "{\"a\":\"}\"}")]
    [InlineData("lixo { nao json } {\"ok\":true}", "{\"ok\":true}")]
    public void FindsFirstValidObject(string text, string expected) => Assert.Equal(expected, JsonExtract.FirstObject(text));

    [Fact]
    public void ReturnsNullWithoutObject() => Assert.Null(JsonExtract.FirstObject("sem json aqui"));
}

public class ModelPickerTests
{
    [Fact]
    public void PicksImageModelsAndPlanningModels()
    {
        string[] models = ["gemini-2.5-flash", "gemini-3-pro-image-preview", "claude-sonnet-4-5", "gemini-2.5-flash-image"];
        Assert.Equal("gemini-3-pro-image-preview", AiSettings.PickModel(models, image: true));
        Assert.Equal("claude-sonnet-4-5", AiSettings.PickModel(models, image: false));
        Assert.Equal("", AiSettings.PickModel(["claude-x"], image: true));
        Assert.Equal("gemini-2.5-flash", AiSettings.PickModel(["gemini-2.5-flash-image", "gemini-2.5-flash"], image: false));
    }
}
