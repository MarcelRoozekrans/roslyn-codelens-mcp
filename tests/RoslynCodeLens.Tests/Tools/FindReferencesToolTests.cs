using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindReferencesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindReferencesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindReferences_ForInterface_ReturnsUsages()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_ForMethod_ReturnsCallSites()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.File.Contains("GreeterConsumer", StringComparison.Ordinal));
    }

    [Fact]
    public void FindReferences_UnknownSymbol_ReturnsEmpty()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "NonExistent");

        Assert.Empty(results);
    }

    [Fact]
    public void FindReferences_MetadataInterface_FindsSourceUsages()
    {
        var results = FindReferencesLogic.Execute(
            _loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(!string.IsNullOrEmpty(r.File)));
    }

    [Fact]
    public void Sort_OrdersByFileThenLine()
    {
        var input = new List<SymbolReference>
        {
            new("Read", "b.cs", 1, "x", "P"),
            new("Read", "a.cs", 9, "x", "P"),
            new("Read", "a.cs", 2, "x", "P"),
        };

        var sorted = FindReferencesTool.Sort(input);

        Assert.Collection(sorted,
            r => { Assert.Equal("a.cs", r.File); Assert.Equal(2, r.Line); },
            r => { Assert.Equal("a.cs", r.File); Assert.Equal(9, r.Line); },
            r => { Assert.Equal("b.cs", r.File); Assert.Equal(1, r.Line); });
    }

    [Fact]
    public void BuildSummary_GroupsByProject()
    {
        var input = new List<SymbolReference>
        {
            new("Read", "a.cs", 1, "x", "Foo"),
            new("Read", "a.cs", 2, "x", "Foo"),
            new("Read", "b.cs", 1, "x", "Bar"),
        };

        var summary = FindReferencesTool.BuildSummary(input);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"Foo\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"Bar\":1", json, StringComparison.Ordinal);
    }
}
