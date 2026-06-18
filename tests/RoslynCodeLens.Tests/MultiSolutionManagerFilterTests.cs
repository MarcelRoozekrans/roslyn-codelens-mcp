using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class MultiSolutionManagerFilterTests
{
    private static string Slnx()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "FilterableSolution", "FilterableSolution.slnx");
        return Path.GetFullPath(path);
    }

    [Fact]
    public async Task LoadSolutionAsync_RepeatedCallWithDifferentFilter_ReplacesPreviousLoad()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        var slnx = Slnx();

        await manager.LoadSolutionAsync(slnx, new ProjectFilter(new[] { "App.*" }, Array.Empty<string>()));
        var firstActiveCount = manager.GetLoadedSolution().Solution.Projects.Count();

        await manager.LoadSolutionAsync(slnx, new ProjectFilter(Array.Empty<string>(), new[] { "Sample.Unrelated" }));
        var secondActiveCount = manager.GetLoadedSolution().Solution.Projects.Count();

        Assert.Equal(5, firstActiveCount);
        Assert.Equal(1, secondActiveCount);
        Assert.Single(manager.ListSolutions());   // replace, not coexist
    }
}
