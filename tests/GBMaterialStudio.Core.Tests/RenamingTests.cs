using Gestaobim.MaterialStudio.Core;
using Xunit;

namespace GBMaterialStudio.Core.Tests;

public class RenamingTests
{
    [Fact]
    public void Plain_find_replace_is_literal_and_ignores_case()
    {
        var rules = new RenameRules { Find = "(cinza)", Replace = "\\1" };
        Assert.Equal("Concreto \\1", Renamer.Apply("Concreto (Cinza)", rules));
    }

    [Fact]
    public void Regex_find_replace_supports_python_style_groups()
    {
        var rules = new RenameRules { Find = @"^(\w+) (\d+)$", Replace = @"\2-\1", UseRegex = true };
        Assert.Equal("12-Grama", Renamer.Apply("Grama 12", rules));
    }

    [Fact]
    public void Invalid_regex_raises_argument_exception()
    {
        Assert.Throws<ArgumentException>(() => Renamer.Apply("x", new RenameRules { Find = "(", UseRegex = true }));
    }

    [Fact]
    public void Collapses_spaces_and_trims() =>
        Assert.Equal("Tinta Branca", Renamer.Apply("  Tinta   Branca ", new RenameRules()));

    [Fact]
    public void Prefix_and_suffix_wrap_the_result() =>
        Assert.Equal("GB - Tinta v2", Renamer.Apply("Tinta", new RenameRules { Prefix = "GB - ", Suffix = " v2" }));

    [Theory]
    [InlineData(CaseMode.Upper, "TINTA DE PAREDE")]
    [InlineData(CaseMode.Lower, "tinta de parede")]
    [InlineData(CaseMode.Title, "Tinta de Parede")]
    [InlineData(CaseMode.Sentence, "Tinta de parede")]
    [InlineData(CaseMode.Keep, "tINTA dE pAREDE")]
    public void Case_modes(CaseMode mode, string expected) =>
        Assert.Equal(expected, Renamer.Apply("tINTA dE pAREDE", new RenameRules { Case = mode }));

    [Fact]
    public void Title_case_keeps_connectors_lowercase_but_not_the_first_word() =>
        Assert.Equal("De Porcelanato da Obra", Renamer.Apply("de porcelanato da obra", new RenameRules { Case = CaseMode.Title }));

    private static List<RenameStatus> Statuses((long, string)[] items, RenameRules rules, string[] all) =>
        Renamer.Plan(items, rules, all).Select(s => s.Status).ToList();

    [Fact]
    public void Unchanged_when_rules_do_nothing() =>
        Assert.Equal([RenameStatus.Unchanged], Statuses([(1, "A")], new RenameRules(), ["A", "B"]));

    [Fact]
    public void Conflict_with_a_material_outside_the_batch() =>
        Assert.Equal([RenameStatus.Conflict], Statuses([(1, "A")], new RenameRules { Find = "A", Replace = "B" }, ["A", "B"]));

    [Fact]
    public void Conflict_ignores_case() =>
        Assert.Equal([RenameStatus.Conflict], Statuses([(1, "A")], new RenameRules { Find = "A", Replace = "b" }, ["A", "B"]));

    [Fact]
    public void Two_items_ending_with_the_same_name_conflict()
    {
        var rules = new RenameRules { Find = @"\d+", Replace = "", UseRegex = true };
        Assert.Equal([RenameStatus.Conflict, RenameStatus.Conflict],
            Statuses([(1, "Grama 1"), (2, "Grama 2")], rules, ["Grama 1", "Grama 2"]));
    }

    [Fact]
    public void Name_freed_by_the_batch_can_be_reused()
    {
        var plan = Renamer.Plan([(1, "A"), (2, "B")], new RenameRules { Find = "B", Replace = "C", IgnoreCase = false }, ["A", "B"]);
        Assert.Equal([RenameStatus.Unchanged, RenameStatus.Ok], plan.Select(s => s.Status));
        var chain = Renamer.Plan([(1, "A"), (2, "B")], new RenameRules { Prefix = "x" }, ["A", "B", "xA"]);
        Assert.Equal([RenameStatus.Conflict, RenameStatus.Ok], chain.Select(s => s.Status));
    }

    [Fact]
    public void Empty_result_is_reported() =>
        Assert.Equal([RenameStatus.Empty], Statuses([(1, "X")], new RenameRules { Find = "X", Replace = "" }, ["X"]));
}

public class DuplicateFinderTests
{
    private static Candidate C(long key, string name, string signature, int usage) => new(key, name, signature, usage);

    [Fact]
    public void Primary_is_the_most_used()
    {
        var groups = DuplicateFinder.Group([C(1, "Grama 2", "verde", 0), C(2, "Grama", "verde", 5), C(3, "Concreto", "cinza", 9)]);
        var group = Assert.Single(groups);
        Assert.Equal("Grama", group.Primary.Name);
        Assert.Equal(["Grama 2"], group.Duplicates.Select(d => d.Name));
    }

    [Fact]
    public void Tie_goes_to_shortest_then_alphabetical_name()
    {
        var group = Assert.Single(DuplicateFinder.Group([C(1, "Tinta B", "s", 1), C(2, "Tinta", "s", 1), C(3, "Tinta A", "s", 1)]));
        Assert.Equal("Tinta", group.Primary.Name);
        Assert.Equal(["Tinta A", "Tinta B"], group.Duplicates.Select(d => d.Name));
    }

    [Fact]
    public void No_duplicates_means_no_groups() =>
        Assert.Empty(DuplicateFinder.Group([C(1, "A", "x", 0), C(2, "B", "y", 0)]));
}
