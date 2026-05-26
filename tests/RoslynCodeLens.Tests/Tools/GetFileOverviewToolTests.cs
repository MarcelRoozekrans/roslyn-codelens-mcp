using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetFileOverviewToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly string _greeterPath;

    public GetFileOverviewToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _greeterPath = _loaded.Solution.Projects
            .First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal))
            .Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal))
            .FilePath!;
    }

    [Fact]
    public async Task ExecuteAsync_ForGreeterFile_ReturnsOverview()
    {
        var result = await GetFileOverviewLogic.ExecuteAsync(
            _loaded, _resolver, _greeterPath, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result.TypesDefined);
        Assert.Contains("Greeter", result.TypesDefined);
        Assert.NotNull(result.Project);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidFile_ThrowsFileNotFound()
    {
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            GetFileOverviewLogic.ExecuteAsync(
                _loaded, _resolver, "nonexistent.cs", CancellationToken.None));

        Assert.Equal(ToolErrorCode.FileNotFound, ex.Code);
    }
}
