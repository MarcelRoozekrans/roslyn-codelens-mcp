using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindReflectionUsageToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindReflectionUsageToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindReflection_DetectsActivatorCreateInstance()
    {
        var results = FindReflectionUsageLogic.Execute(_loaded, _resolver, null);

        Assert.Contains(results, r => string.Equals(r.Kind, "dynamic_instantiation", StringComparison.Ordinal)
            && r.Snippet.Contains("Activator.CreateInstance", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReflection_DetectsTypeGetType()
    {
        var results = FindReflectionUsageLogic.Execute(_loaded, _resolver, null);

        Assert.Contains(results, r => r.Snippet.Contains("Type.GetType", StringComparison.Ordinal));
    }

    [Fact]
    public void Sort_OrdersByFileThenLine()
    {
        var input = new List<ReflectionUsage>
        {
            new("method_invoke", "X", "b.cs", 1, "x"),
            new("method_invoke", "X", "a.cs", 9, "x"),
            new("method_invoke", "X", "a.cs", 2, "x"),
        };

        var sorted = FindReflectionUsageTool.Sort(input);

        Assert.Collection(sorted,
            r => { Assert.Equal("a.cs", r.File); Assert.Equal(2, r.Line); },
            r => { Assert.Equal("a.cs", r.File); Assert.Equal(9, r.Line); },
            r => { Assert.Equal("b.cs", r.File); Assert.Equal(1, r.Line); });
    }

    [Fact]
    public void BuildSummary_GroupsByKind()
    {
        var input = new List<ReflectionUsage>
        {
            new("method_invoke",         "X", "a.cs", 1, "x"),
            new("method_invoke",         "Y", "a.cs", 2, "x"),
            new("dynamic_instantiation", "Z", "b.cs", 1, "x"),
        };

        var summary = FindReflectionUsageTool.BuildSummary(input);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"method_invoke\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"dynamic_instantiation\":1", json, StringComparison.Ordinal);
    }
}
