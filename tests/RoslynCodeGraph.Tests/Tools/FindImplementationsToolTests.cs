using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindImplementationsToolTests : IAsyncLifetime
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
    public void FindImplementations_ForInterface_ReturnsImplementors()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, "IGreeter");

        Assert.Contains(results, r => r.FullName.Contains("Greeter"));
        Assert.True(results.Count >= 1);
    }

    [Fact]
    public void FindImplementations_ForBaseClass_ReturnsDerived()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, "Greeter");

        Assert.Contains(results, r => r.FullName.Contains("FancyGreeter"));
    }
}
