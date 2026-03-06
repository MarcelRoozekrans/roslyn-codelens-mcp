using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetProjectDependenciesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void GetDependencies_ForTestLib2_ReturnsTestLib()
    {
        var result = GetProjectDependenciesLogic.Execute(_loaded, "TestLib2");

        Assert.NotNull(result);
        Assert.Contains(result.Direct, d => d.Name == "TestLib");
    }
}
