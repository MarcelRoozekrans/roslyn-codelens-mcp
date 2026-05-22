using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class SearchSymbolsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public SearchSymbolsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void SearchSymbols_ByTypeName_FindsTypes()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, _metadata, "Greeter");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.FullName.Contains("Greeter", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchSymbols_ByMethodName_FindsMethods()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, _metadata, "Greet");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => string.Equals(r.Type, "method", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchSymbols_CaseInsensitive_Works()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, _metadata, "greeter");

        Assert.NotEmpty(results);
    }

    [Fact]
    public void SearchSymbols_NoMatch_ReturnsEmpty()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, _metadata, "XyzNonExistent123");

        Assert.Empty(results);
    }

    [Fact]
    public void Search_MatchesMetadataSymbol()
    {
        var results = SearchSymbolsLogic.Execute(_resolver, _metadata, "IServiceCollection");
        Assert.Contains(
            results,
            r => string.Equals(r.Origin?.Kind, "metadata", StringComparison.Ordinal)
              && string.Equals(r.FullName, "Microsoft.Extensions.DependencyInjection.IServiceCollection", StringComparison.Ordinal));
    }

    [Fact]
    public void SortByMatchQuality_ExactBeforePrefixBeforeSubstring()
    {
        var input = new List<SymbolLocation>
        {
            new("class", "Foo.MyBarBaz", "x.cs", 1, "P"),    // substring
            new("class", "Foo.Bar",      "x.cs", 1, "P"),    // exact
            new("class", "Foo.BarHelper","x.cs", 1, "P"),    // prefix
        };

        var sorted = SearchSymbolsTool.SortByMatchQuality("Bar", input);

        Assert.Collection(sorted,
            s => Assert.Equal("Foo.Bar", s.FullName),
            s => Assert.Equal("Foo.BarHelper", s.FullName),
            s => Assert.Equal("Foo.MyBarBaz", s.FullName));
    }

    [Fact]
    public void BuildSummary_GroupsByKind()
    {
        var input = new List<SymbolLocation>
        {
            new("class",  "A", "x.cs", 1, "P"),
            new("class",  "B", "x.cs", 2, "P"),
            new("method", "C", "x.cs", 3, "P"),
        };

        var summary = SearchSymbolsTool.BuildSummary(input);
        var json = System.Text.Json.JsonSerializer.Serialize(summary);

        Assert.Contains("\"class\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"method\":1", json, StringComparison.Ordinal);
    }
}
