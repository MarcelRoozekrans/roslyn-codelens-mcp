using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetComplexityMetricsToolTests : IAsyncLifetime
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
    public void GetComplexityMetrics_AllMethods_ReturnsResults()
    {
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void GetComplexityMetrics_HighThreshold_ReturnsEmpty()
    {
        var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 100);
        Assert.Empty(results);
    }

    [Fact]
    public void GetComplexityMetrics_ProjectFilter_FiltersResults()
    {
        var all = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0);
        var filtered = GetComplexityMetricsLogic.Execute(_loaded, _resolver, "TestLib2", 0);
        Assert.All(filtered, r => Assert.Equal("TestLib2", r.Project));
    }
}
