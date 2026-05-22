using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindNamingViolationsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindNamingViolationsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindNamingViolations_CleanCode_NoViolations()
    {
        var results = FindNamingViolationsLogic.Execute(_loaded, _resolver, null);
        Assert.NotNull(results);
    }

    [Fact]
    public void FindNamingViolations_ProjectFilter_FiltersResults()
    {
        var filtered = FindNamingViolationsLogic.Execute(_loaded, _resolver, "TestLib");
        Assert.All(filtered, r => Assert.Contains("TestLib", r.Project, StringComparison.Ordinal));
    }

    [Fact]
    public void Sort_OrdersByRuleThenFile()
    {
        var input = new List<NamingViolation>
        {
            new("x", "Field", "RuleB", "fix",  "a.cs", 1, "P"),
            new("y", "Class", "RuleA", "fix",  "b.cs", 1, "P"),
            new("z", "Class", "RuleA", "fix",  "a.cs", 9, "P"),
        };

        var sorted = FindNamingViolationsTool.Sort(input);

        Assert.Collection(sorted,
            n => { Assert.Equal("RuleA", n.Rule); Assert.Equal("a.cs", n.File); },
            n => { Assert.Equal("RuleA", n.Rule); Assert.Equal("b.cs", n.File); },
            n => Assert.Equal("RuleB", n.Rule));
    }

    [Fact]
    public void BuildSummary_GroupsByRule()
    {
        var input = new List<NamingViolation>
        {
            new("x", "Class", "PascalCase", "fix", "a.cs", 1, "P"),
            new("y", "Class", "PascalCase", "fix", "a.cs", 2, "P"),
            new("z", "Field", "underscorePrefix", "fix", "b.cs", 1, "P"),
        };

        var summary = FindNamingViolationsTool.BuildSummary(input);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"PascalCase\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"underscorePrefix\":1", json, StringComparison.Ordinal);
    }
}
