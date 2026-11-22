using System.IO.Compression;
using Gestaobim.DwgExport.Core;
using Xunit;

namespace GBDwgExport.Core.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gbdwg-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void SettingsRoundTrip()
    {
        var path = Path.Combine(_folder, "s.json");
        new ExportSettings
        {
            Folder = @"D:\Entrega",
            Dxf = true,
            Mode = OutputMode.MultipleLayouts,
            Pattern = NamePattern.NumberOnly,
            RevitSetup = "Setup A",
            OpenFolder = false,
        }.Save(path);

        var loaded = ExportSettings.Load(path);
        Assert.Equal(@"D:\Entrega", loaded.Folder);
        Assert.True(loaded.Dxf);
        Assert.Equal(OutputMode.MultipleLayouts, loaded.Mode);
        Assert.Equal(NamePattern.NumberOnly, loaded.Pattern);
        Assert.Equal("Setup A", loaded.RevitSetup);
        Assert.False(loaded.OpenFolder);
    }

    [Fact]
    public void MissingOrBrokenFileGivesDefaults()
    {
        // The default result is one drawing with a layout per sheet, not one file per sheet.
        var missing = ExportSettings.Load(Path.Combine(_folder, "nope.json"));
        Assert.Equal(OutputMode.MultipleLayouts, missing.Mode);

        var broken = Path.Combine(_folder, "broken.json");
        File.WriteAllText(broken, "{ not json");
        Assert.Equal(OutputMode.MultipleLayouts, ExportSettings.Load(broken).Mode);
    }

    [Fact]
    public void TransferPackageRoundTrips()
    {
        var zip = Path.Combine(_folder, "p.zip");
        SettingsTransfer.Export(new ExportSettings { Folder = "X", RevitSetup = "S" }, zip, "0.1.0");

        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("settings.json"));
        }
        var imported = SettingsTransfer.Import(zip);
        Assert.Equal("X", imported.Folder);
        Assert.Equal("S", imported.RevitSetup);
    }

    [Fact]
    public void ImportRejectsForeignAndBrokenPackages()
    {
        var foreign = Path.Combine(_folder, "foreign.zip");
        using (var archive = ZipFile.Open(foreign, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
            writer.Write("{\"product\":\"Other\"}");
        }
        Assert.Throws<TransferException>(() => SettingsTransfer.Import(foreign));

        var notZip = Path.Combine(_folder, "x.zip");
        File.WriteAllText(notZip, "nope");
        Assert.Throws<TransferException>(() => SettingsTransfer.Import(notZip));
    }
}

public class UpdateTests
{
    private static string Release(string tag, bool pre = false, bool draft = false, string url = "https://github.com/valber99/Gestaobim-plugins/releases/tag/x") =>
        "{\"tag_name\":\"" + tag + "\",\"prerelease\":" + pre.ToString().ToLower() + ",\"draft\":" + draft.ToString().ToLower() +
        ",\"html_url\":\"" + url + "\"}";

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Theory]
    [InlineData("v0.2.1", 0, 2, 1)]
    [InlineData("v1.0", 1, 0, 0)]
    [InlineData("V1.2.3-beta", 1, 2, 3)]
    public void ParsesPrefixedTags(string tag, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build, 0), UpdateRules.ParseVersion(tag));

    [Theory]
    [InlineData("dwg-v0.3.1")]
    [InlineData("vabc")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherTagsAreNotThisPlugin(string? tag) => Assert.Null(UpdateRules.ParseVersion(tag));

    [Fact]
    public void PicksTheNewestOfThisPluginAndIgnoresTheOthers()
    {
        var json = List(Release("dwg-v9.0.0"), Release("v0.2.0"), Release("v0.3.0"), Release("v0.1.0"));
        Assert.Equal("v0.3.0", UpdateRules.ParseNewer(json, new Version(0, 1, 0, 0))?.Tag);
    }

    [Fact]
    public void SameOrOlderIsIgnored()
    {
        var json = List(Release("v0.1.0"));
        Assert.Null(UpdateRules.ParseNewer(json, new Version(0, 1, 0, 0)));
        Assert.Null(UpdateRules.ParseNewer(json, new Version(0, 2, 0, 0)));
    }

    [Fact]
    public void PrereleaseDraftForeignUrlAndGarbageAreIgnored()
    {
        var current = new Version(0, 1, 0, 0);
        Assert.Null(UpdateRules.ParseNewer(List(Release("v9.0.0", pre: true)), current));
        Assert.Null(UpdateRules.ParseNewer(List(Release("v9.0.0", draft: true)), current));
        Assert.Null(UpdateRules.ParseNewer(List(Release("v9.0.0", url: "https://evil.example/x")), current));
        Assert.Null(UpdateRules.ParseNewer("not json", current));
        Assert.Null(UpdateRules.ParseNewer("{}", current));
    }

    [Fact]
    public void ChecksAtMostOncePerInterval()
    {
        var state = new UpdateState { LastCheckUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc) };
        Assert.False(state.Due(state.LastCheckUtc.AddHours(23)));
        Assert.True(state.Due(state.LastCheckUtc.AddHours(25)));
    }
}

public class CadcraftOutputTests
{
    [Fact]
    public void ReadsExtents()
    {
        var extents = CadcraftOutput.ParseExtents("{\"extents\":[[-1.5,2],[10,20.5]]}");
        Assert.Equal((-1.5, 2.0, 10.0, 20.5), extents);
    }

    [Theory]
    [InlineData("{\"extents\":null}")]
    [InlineData("{}")]
    [InlineData("{\"extents\":[[0,0],[0,0]]}")]
    [InlineData("{\"extents\":[[5,5],[1,1]]}")]
    [InlineData("{\"extents\":[[0,0]]}")]
    public void EmptyOrInvalidExtentsAreNull(string json) => Assert.Null(CadcraftOutput.ParseExtents(json));

    [Fact]
    public void ReadsTheSummary()
    {
        var summary = CadcraftOutput.ParseSummary("{\"entityCount\":12,\"layers\":[\"a\",\"b\"],\"blocks\":[\"x\"],\"layouts\":[\"L1\",\"L2\"]}");
        Assert.NotNull(summary);
        Assert.Equal(12, summary!.Entities);
        Assert.Equal(2, summary.Layers);
        Assert.Equal(1, summary.Blocks);
        Assert.Equal(["L1", "L2"], summary.Layouts);
    }

    [Fact]
    public void LossesComeFromTheToolsOwnWarnings()
    {
        var stderr = "wrote x.dwg\nNot saved in the DWG file (its writer can't write these object types yet): 114 ARC_DIMENSION\nwarning: font substituted\n";
        var losses = CadcraftOutput.ParseLosses(stderr);
        Assert.Equal(2, losses.Count);
        Assert.Contains("114 ARC_DIMENSION", losses[0]);
        Assert.Empty(CadcraftOutput.ParseLosses("wrote x.dwg\n"));
    }
}
