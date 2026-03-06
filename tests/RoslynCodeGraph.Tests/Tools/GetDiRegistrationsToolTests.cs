using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetDiRegistrationsToolTests : IAsyncLifetime
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
    public void FindDiRegistrations_ForIGreeter_ReturnsRegistration()
    {
        var results = GetDiRegistrationsLogic.Execute(_loaded, _resolver, "IGreeter");

        Assert.Single(results);
        Assert.Equal("Scoped", results[0].Lifetime);
        Assert.Contains("Greeter", results[0].Implementation);
    }
}
