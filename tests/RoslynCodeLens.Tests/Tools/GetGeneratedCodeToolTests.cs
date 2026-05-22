using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetGeneratedCodeToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetGeneratedCodeToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Execute_ReturnsEmpty_WhenFileNotFound()
    {
        var results = GetGeneratedCodeLogic.Execute(_loaded, _resolver, null, "nonexistent.g.cs");
        Assert.Empty(results);
    }

    [Fact]
    public void Execute_ReturnsEmpty_WhenGeneratorNotFound()
    {
        var results = GetGeneratedCodeLogic.Execute(_loaded, _resolver, "NonExistentGenerator", null);
        Assert.Empty(results);
    }

    [Fact]
    public void Sort_OrdersByProjectThenFilePath()
    {
        var input = new List<GeneratedFileInfo>
        {
            new("z.g.cs", "Bar", null, Array.Empty<string>(), ""),
            new("b.g.cs", "Foo", null, Array.Empty<string>(), ""),
            new("a.g.cs", "Foo", null, Array.Empty<string>(), ""),
        };

        var sorted = GetGeneratedCodeTool.Sort(input);

        Assert.Collection(sorted,
            g => Assert.Equal("Bar", g.Project),
            g => { Assert.Equal("Foo", g.Project); Assert.Equal("a.g.cs", g.FilePath); },
            g => { Assert.Equal("Foo", g.Project); Assert.Equal("b.g.cs", g.FilePath); });
    }
}
