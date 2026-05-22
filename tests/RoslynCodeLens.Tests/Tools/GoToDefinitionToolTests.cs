using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GoToDefinitionToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public GoToDefinitionToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void GoToDefinition_ForType_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "Greeter");

        Assert.Single(results);
        Assert.Contains("Greeter.cs", results[0].File, StringComparison.Ordinal);
        Assert.Equal("class", results[0].Type);
        Assert.Equal("source", results[0].Origin?.Kind);
    }

    [Fact]
    public void GoToDefinition_ForInterface_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "IGreeter");

        Assert.Single(results);
        Assert.Contains("IGreeter.cs", results[0].File, StringComparison.Ordinal);
        Assert.Equal("interface", results[0].Type);
    }

    [Fact]
    public void GoToDefinition_ForMethod_ReturnsLocation()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "Greeter.Greet");

        Assert.NotEmpty(results);
        Assert.Equal("method", results[0].Type);
    }

    [Fact]
    public void GoToDefinition_UnknownSymbol_ReturnsEmpty()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "NonExistent");

        Assert.Empty(results);
    }

    [Fact]
    public void GoToDefinition_MetadataType_ReturnsMetadataOrigin()
    {
        var result = GoToDefinitionLogic.Execute(
            _resolver, _metadata, "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        var single = Assert.Single(result);
        Assert.NotNull(single.Origin);
        Assert.Equal("metadata", single.Origin!.Kind);
        Assert.Equal("", single.File);
        Assert.Equal(0, single.Line);
    }

    [Fact]
    public void Sort_OrdersByFileThenLine()
    {
        var input = new List<SymbolLocation>
        {
            new("class", "B", "b.cs", 1, "P"),
            new("class", "A2", "a.cs", 9, "P"),
            new("class", "A1", "a.cs", 2, "P"),
        };

        var sorted = GoToDefinitionTool.Sort(input);

        Assert.Collection(sorted,
            s => { Assert.Equal("a.cs", s.File); Assert.Equal(2, s.Line); },
            s => { Assert.Equal("a.cs", s.File); Assert.Equal(9, s.Line); },
            s => { Assert.Equal("b.cs", s.File); Assert.Equal(1, s.Line); });
    }
}
