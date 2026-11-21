using Gestaobim.MaterialStudio.Core;
using Xunit;

namespace GBMaterialStudio.Core.Tests;

public class UpdateTests
{
    private static string Release(string tag, bool pre = false, bool draft = false,
        string url = "https://github.com/valber99/Gestaobim-plugins/releases/tag/x") =>
        "{\"tag_name\":\"" + tag + "\",\"prerelease\":" + pre.ToString().ToLower() +
        ",\"draft\":" + draft.ToString().ToLower() + ",\"html_url\":\"" + url + "\",\"body\":\"n\"}";

    [Theory]
    [InlineData("v0.3.1", 0, 3, 1)]
    [InlineData("0.4", 0, 4, 0)]
    [InlineData("v1.0.0-beta", 1, 0, 0)]
    public void ParsesTags(string tag, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build, 0), UpdateRules.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void RejectsBadTags(string? tag) => Assert.Null(UpdateRules.ParseVersion(tag));

    [Fact]
    public void NewerReleaseIsReported() =>
        Assert.Equal("v0.3.1", UpdateRules.ParseNewer(Release("v0.3.1"), new Version(0, 3, 0, 0))?.Tag);

    [Fact]
    public void SameOrOlderIsIgnored()
    {
        Assert.Null(UpdateRules.ParseNewer(Release("v0.3.0"), new Version(0, 3, 0, 0)));
        Assert.Null(UpdateRules.ParseNewer(Release("v0.2.0"), new Version(0, 3, 0, 0)));
    }

    [Fact]
    public void PrereleaseDraftAndForeignUrlAreIgnored()
    {
        var current = new Version(0, 3, 0, 0);
        Assert.Null(UpdateRules.ParseNewer(Release("v9.0.0", pre: true), current));
        Assert.Null(UpdateRules.ParseNewer(Release("v9.0.0", draft: true), current));
        Assert.Null(UpdateRules.ParseNewer(Release("v9.0.0", url: "https://evil.example/x"), current));
        Assert.Null(UpdateRules.ParseNewer("not json", current));
    }

    [Fact]
    public void ChecksAtMostOncePerInterval()
    {
        var state = new UpdateState { LastCheckUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc) };
        Assert.False(state.Due(state.LastCheckUtc.AddHours(23)));
        Assert.True(state.Due(state.LastCheckUtc.AddHours(25)));
    }
}
