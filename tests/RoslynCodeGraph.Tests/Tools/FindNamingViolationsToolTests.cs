using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindNamingViolationsToolTests : IAsyncLifetime
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
    public void FindNamingViolations_CleanCode_NoViolations()
    {
        var results = FindNamingViolationsLogic.Execute(_loaded, _resolver, null);
        Assert.NotNull(results);
    }

    [Fact]
    public void FindNamingViolations_ProjectFilter_FiltersResults()
    {
        var filtered = FindNamingViolationsLogic.Execute(_loaded, _resolver, "TestLib");
        Assert.All(filtered, r => Assert.Contains("TestLib", r.Project));
    }
}
