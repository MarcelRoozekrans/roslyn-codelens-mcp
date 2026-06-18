using Microsoft.CodeAnalysis;
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class SolutionLoaderFilterTests
{
    private static string Slnx()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "FilterableSolution", "FilterableSolution.slnx");
        return Path.GetFullPath(path);
    }

    [Fact]
    public async Task OpenAsync_IncludeGlob_LoadsMatchingAndTransitive()
    {
        var loader = new SolutionLoader();
        var filter = new ProjectFilter(Include: new[] { "App.*" }, RootProjects: Array.Empty<string>());

        var (solution, workspace, skipped) = await loader.OpenAsync(Slnx(), filter);
        using var _ = workspace;

        var loadedNames = solution.Projects.Select(p => p.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "App.Api", "App.Api.Tests", "App.Domain", "App.Infrastructure", "Shared.Common" },
            loadedNames);
        Assert.Single(skipped, s => s.Name == "Sample.Unrelated");
    }

    [Fact]
    public async Task OpenAsync_RootProjects_LoadsClosure()
    {
        var loader = new SolutionLoader();
        var filter = new ProjectFilter(Include: Array.Empty<string>(), RootProjects: new[] { "App.Api" });

        var (solution, workspace, skipped) = await loader.OpenAsync(Slnx(), filter);
        using var _ = workspace;

        var loadedNames = solution.Projects.Select(p => p.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "App.Api", "App.Domain", "App.Infrastructure", "Shared.Common" },
            loadedNames);
        Assert.Equal(2, skipped.Count);   // Sample.Unrelated + App.Api.Tests
    }

    [Fact]
    public async Task OpenAsync_NoFilter_BehavesAsBefore()
    {
        var loader = new SolutionLoader();
        var (solution, workspace, _) = await loader.OpenAsync(Slnx(), filter: null);
        using var _ws = workspace;

        Assert.Equal(6, solution.Projects.Count());
    }
}
