using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetGeneratedCodeToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
}
