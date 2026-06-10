using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindImplementationsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public FindImplementationsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindImplementations_ForInterface_ReturnsImplementors()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");

        Assert.Contains(results, r => r.FullName.Contains("Greeter", StringComparison.Ordinal));
        Assert.True(results.Count >= 1);
    }

    [Fact]
    public void FindImplementations_ForBaseClass_ReturnsDerived()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, _metadata, "Greeter");

        Assert.Contains(results, r => r.FullName.Contains("FancyGreeter", StringComparison.Ordinal));
    }

    [Fact]
    public void FindImplementations_MetadataInterface_FindsSourceImplementors()
    {
        var results = FindImplementationsLogic.Execute(
            _loaded, _resolver, _metadata, "System.IDisposable");

        Assert.Contains(results, r => r.FullName.EndsWith("Greeter", StringComparison.Ordinal));
    }

    [Fact]
    public void FindImplementations_ForInterface_FindsImplementorInReferencingProject()
    {
        // IGreeter is defined in TestLib; CrossProjectGreeter implements it in TestLib2.
        // Without cross-compilation symbol normalisation, only same-project implementors
        // are found because Roslyn produces different ISymbol instances per compilation.
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");

        Assert.Contains(results, r => r.FullName.Contains("CrossProjectGreeter", StringComparison.Ordinal));
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

        var sorted = FindImplementationsTool.Sort(input);

        Assert.Collection(sorted,
            s => { Assert.Equal("a.cs", s.File); Assert.Equal(2, s.Line); },
            s => { Assert.Equal("a.cs", s.File); Assert.Equal(9, s.Line); },
            s => { Assert.Equal("b.cs", s.File); Assert.Equal(1, s.Line); });
    }
}
