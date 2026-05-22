using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindUnusedSymbolsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindUnusedSymbolsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindUnusedSymbols_ReturnsResults()
    {
        var results = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, null, false);
        Assert.NotNull(results);
    }

    [Fact]
    public void FindUnusedSymbols_ProjectFilter_FiltersResults()
    {
        var results = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
        Assert.All(results, r => Assert.Contains("TestLib", r.Project, StringComparison.Ordinal));
    }

    [Fact]
    public void Sort_OrdersByProjectThenFile()
    {
        var input = new List<UnusedSymbolInfo>
        {
            new("X", "Class",  "z.cs", 1, "Bar"),
            new("X", "Class",  "b.cs", 1, "Foo"),
            new("X", "Method", "a.cs", 9, "Foo"),
        };

        var sorted = FindUnusedSymbolsTool.Sort(input);

        Assert.Collection(sorted,
            u => Assert.Equal("Bar", u.Project),
            u => { Assert.Equal("Foo", u.Project); Assert.Equal("a.cs", u.File); },
            u => { Assert.Equal("Foo", u.Project); Assert.Equal("b.cs", u.File); });
    }

    [Fact]
    public void BuildSummary_GroupsByKind()
    {
        var input = new List<UnusedSymbolInfo>
        {
            new("X", "Class",  "a.cs", 1, "P"),
            new("Y", "Class",  "a.cs", 2, "P"),
            new("Z", "Method", "a.cs", 3, "P"),
        };

        var summary = FindUnusedSymbolsTool.BuildSummary(input);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"Class\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"Method\":1", json, StringComparison.Ordinal);
    }
}
