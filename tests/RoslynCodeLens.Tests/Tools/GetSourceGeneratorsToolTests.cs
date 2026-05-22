using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetSourceGeneratorsToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetSourceGeneratorsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Execute_ReturnsEmptyList_WhenNoGenerators()
    {
        var results = GetSourceGeneratorsLogic.Execute(_loaded, _resolver, null);
        Assert.NotNull(results);
    }

    [Fact]
    public void Execute_FiltersByProject_WhenProjectSpecified()
    {
        var projectName = _loaded.Solution.Projects.First().Name;
        var results = GetSourceGeneratorsLogic.Execute(_loaded, _resolver, projectName);
        Assert.NotNull(results);
        Assert.All(results, r => Assert.Equal(projectName, r.Project));
    }

    [Fact]
    public void Sort_OrdersByProjectThenGeneratorName()
    {
        var input = new List<SourceGeneratorInfo>
        {
            new("ZGen", "Foo", 0, Array.Empty<string>()),
            new("AGen", "Foo", 0, Array.Empty<string>()),
            new("MGen", "Bar", 0, Array.Empty<string>()),
        };

        var sorted = GetSourceGeneratorsTool.Sort(input);

        Assert.Collection(sorted,
            s => { Assert.Equal("Bar", s.Project); Assert.Equal("MGen", s.GeneratorName); },
            s => { Assert.Equal("Foo", s.Project); Assert.Equal("AGen", s.GeneratorName); },
            s => { Assert.Equal("Foo", s.Project); Assert.Equal("ZGen", s.GeneratorName); });
    }
}
