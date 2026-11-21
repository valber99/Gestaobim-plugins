using Gestaobim.DwgExport.Core;
using Xunit;

namespace GBDwgExport.Core.Tests;

public class NamingTests
{
    [Theory]
    [InlineData("Planta Baixa", "Planta Baixa")]
    [InlineData("Situação, Locação", "Situacao, Locacao")]
    [InlineData("A/B:C*D?E\"F<G>H|I", "A_B_C_D_E_F_G_H_I")]
    [InlineData("  nome com ponto. ", "nome com ponto")]
    [InlineData("", "Export")]
    [InlineData("   ", "Export")]
    [InlineData(null, "Export")]
    [InlineData("...", "Export")]
    public void SanitizeKeepsNamesSafe(string? input, string expected) =>
        Assert.Equal(expected, FileNamer.Sanitize(input));

    [Fact]
    public void SanitizeReplacesControlCharacters() =>
        Assert.Equal("a_b", FileNamer.Sanitize("a\tb"));

    [Fact]
    public void ForSheetFollowsThePattern()
    {
        Assert.Equal("A101 - Planta Térrea".Replace("é", "e"), FileNamer.ForSheet("A101", "Planta Térrea", NamePattern.NumberAndName));
        Assert.Equal("A101", FileNamer.ForSheet("A101", "Planta Térrea", NamePattern.NumberOnly));
        Assert.Equal("A101", FileNamer.ForSheet("A101", "", NamePattern.NumberAndName));
        Assert.Equal("Export - Corte", FileNamer.ForSheet("", "Corte", NamePattern.NumberAndName));
    }

    [Fact]
    public void UniqueAddsSuffixIgnoringCase()
    {
        var used = new HashSet<string>();
        Assert.Equal("A101", FileNamer.Unique("A101", used));
        Assert.Equal("a101_2", FileNamer.Unique("a101", used));
        Assert.Equal("A101_3", FileNamer.Unique("A101", used));
        Assert.Equal("A102", FileNamer.Unique("A102", used));
    }

    [Fact]
    public void TooLongFindsPathsOverTheLimit()
    {
        var folder = @"C:\" + new string('p', 200);
        var fine = new string('a', 20);
        var tooLong = new string('b', 60);
        var result = FileNamer.TooLong(folder, [fine, tooLong], ".dwg");
        Assert.Equal([tooLong], result);
    }

    [Fact]
    public void TooLongCountsTheSeparatorAndExtension()
    {
        var folder = @"C:\x";
        var exactly = new string('n', FileNamer.MaxPath - folder.Length - 1 - ".dwg".Length);
        Assert.Empty(FileNamer.TooLong(folder, [exactly], ".dwg"));
        Assert.Single(FileNamer.TooLong(folder, [exactly + "n"], ".dwg"));
        Assert.Single(FileNamer.TooLong(folder + "\\", [exactly + "n"], ".dwg"));
    }

    [Fact]
    public void NaturalComparerOrdersNumbersByValue()
    {
        var sorted = new[] { "A10", "A2", "A1", "a3", "B1", "A02" }
            .OrderBy(x => x, NaturalComparer.Instance).ToArray();
        Assert.Equal(["A1", "A2", "A02", "a3", "A10", "B1"], sorted);
    }

    [Theory]
    [InlineData("A2", "A10", -1)]
    [InlineData("A10", "A2", 1)]
    [InlineData("A1", "a1", 0)]
    [InlineData("A", "A1", -1)]
    [InlineData("1", "A", -1)]
    public void NaturalComparerCompares(string x, string y, int sign) =>
        Assert.Equal(sign, Math.Sign(NaturalComparer.Instance.Compare(x, y)));

    [Fact]
    public void NaturalComparerHandlesNull()
    {
        Assert.True(NaturalComparer.Instance.Compare(null, "A") < 0);
        Assert.True(NaturalComparer.Instance.Compare("A", null) > 0);
        Assert.Equal(0, NaturalComparer.Instance.Compare(null, null));
    }
}
