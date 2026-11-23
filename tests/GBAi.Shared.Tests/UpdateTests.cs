using Gestaobim.Ai;
using Xunit;

namespace GBAi.Shared.Tests;

public class AiUpdateRulesTests
{
    private static string Release(string tag, bool draft = false, bool pre = false, string url = "https://github.com/valber99/Gestaobim-plugins/releases/tag/") =>
        $$"""{"tag_name":"{{tag}}","draft":{{(draft ? "true" : "false")}},"prerelease":{{(pre ? "true" : "false")}},"html_url":"{{url}}{{tag}}"}""";

    [Theory]
    [InlineData("v0.5.0", "0.5.0.0")]
    [InlineData("V1.2", "1.2.0.0")]
    [InlineData("v0.5.1-beta", "0.5.1.0")]
    [InlineData("v2.0.0+build7", "2.0.0.0")]
    public void ParsesTags(string tag, string expected) => Assert.Equal(Version.Parse(expected), AiUpdateRules.ParseVersion(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.5.0")]
    [InlineData("dwg-v0.5.0")]
    [InlineData("vabc")]
    public void RejectsOtherTags(string? tag) => Assert.Null(AiUpdateRules.ParseVersion(tag));

    [Fact]
    public void PicksNewestStableRelease()
    {
        var json = $"[{Release("v0.5.0")},{Release("v0.7.0", draft: true)},{Release("v0.6.1")},{Release("v0.8.0", pre: true)},{Release("v0.6.0")}]";
        var newer = AiUpdateRules.ParseNewer(json, new Version(0, 5, 0));
        Assert.NotNull(newer);
        Assert.Equal(new Version(0, 6, 1, 0), newer!.Version);
        Assert.Equal("v0.6.1", newer.Tag);
    }

    [Fact]
    public void NothingWhenInstalledIsCurrentOrNewer()
    {
        var json = $"[{Release("v0.5.0")}]";
        Assert.Null(AiUpdateRules.ParseNewer(json, new Version(0, 5, 0)));
        Assert.Null(AiUpdateRules.ParseNewer(json, new Version(0, 5, 0, 0)));
        Assert.Null(AiUpdateRules.ParseNewer(json, new Version(0, 6)));
    }

    [Fact]
    public void IgnoresLinksOutsideGithub()
    {
        var json = $"[{Release("v9.0.0", url: "https://exemplo.invalido/")},{Release("v0.6.0", url: "http://github.com/")}]";
        Assert.Null(AiUpdateRules.ParseNewer(json, new Version(0, 5, 0)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("nao e json")]
    [InlineData("""[{"tag_name":5,"html_url":"https://github.com/x"}]""")]
    [InlineData("""[{"tag_name":"v1.0.0","draft":"sim","html_url":"https://github.com/x"}]""")]
    [InlineData("[1,null,\"x\"]")]
    public void MalformedAnswerMeansNoUpdate(string json) => Assert.Null(AiUpdateRules.ParseNewer(json, new Version(0, 5, 0)));
}

public class AiUpdateStateTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gbai-upd-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("GBAI_HOME");

    public AiUpdateStateTests() => Environment.SetEnvironmentVariable("GBAI_HOME", _home);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GBAI_HOME", _previous);
        try { Directory.Delete(_home, true); } catch (Exception) { }
    }

    [Fact]
    public void FirstRunIsDueAndSavedStateRoundTrips()
    {
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var fresh = AiUpdateState.Load();
        Assert.True(fresh.Due(now));

        new AiUpdateState { LastCheckUtc = now }.Save();
        var loaded = AiUpdateState.Load();
        Assert.Equal(now, loaded.LastCheckUtc.ToUniversalTime());
        Assert.False(loaded.Due(now.AddHours(23)));
        Assert.True(loaded.Due(now.AddHours(24)));
    }

    [Fact]
    public void CorruptFileIsTreatedAsNeverChecked()
    {
        Directory.CreateDirectory(GbAiPaths.SettingsDir);
        File.WriteAllText(Path.Combine(GbAiPaths.SettingsDir, "update-check.json"), "{lixo");
        Assert.Equal(DateTime.MinValue, AiUpdateState.Load().LastCheckUtc);
        File.WriteAllText(Path.Combine(GbAiPaths.SettingsDir, "update-check.json"), """{"last_check_utc":42}""");
        Assert.Equal(DateTime.MinValue, AiUpdateState.Load().LastCheckUtc);
    }
}
