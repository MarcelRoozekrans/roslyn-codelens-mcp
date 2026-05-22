using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class FindCircularDependenciesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public FindCircularDependenciesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void FindCircularDependencies_NoCycles_ReturnsEmpty()
    {
        var results = FindCircularDependenciesLogic.Execute(_loaded, _resolver, "project");
        Assert.Empty(results);
    }

    [Fact]
    public void FindCircularDependencies_InvalidLevel_ReturnsEmpty()
    {
        var results = FindCircularDependenciesLogic.Execute(_loaded, _resolver, "invalid");
        Assert.Empty(results);
    }

    [Fact]
    public void Sort_OrdersByCycleLengthDesc()
    {
        var input = new List<CircularDependency>
        {
            new("project", new[] { "A", "B" }),
            new("project", new[] { "A", "B", "C", "D" }),
            new("project", new[] { "A", "B", "C" }),
        };

        var sorted = FindCircularDependenciesTool.Sort(input);

        Assert.Collection(sorted,
            c => Assert.Equal(4, c.Cycle.Count),
            c => Assert.Equal(3, c.Cycle.Count),
            c => Assert.Equal(2, c.Cycle.Count));
    }
}
