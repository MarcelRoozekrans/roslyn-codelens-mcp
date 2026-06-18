using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class LoadSolutionToolFilterTests
{
    private static string Slnx()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "FilterableSolution", "FilterableSolution.slnx");
        return Path.GetFullPath(path);
    }

    [Fact]
    public async Task Execute_WithInclude_ReturnsLoadedAndSkippedCounts()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();
        var result = (string)await LoadSolutionTool.Execute(manager, store, Slnx(),
            include: new[] { "App.*" }, rootProjects: null);

        Assert.Contains("Loaded 5", result);
        Assert.Contains("skipped 1", result);
    }

    [Fact]
    public async Task Execute_NoFilter_BehavesAsBefore()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();
        var result = (string)await LoadSolutionTool.Execute(manager, store, Slnx(),
            include: null, rootProjects: null);

        Assert.Contains("Loaded", result);
        Assert.DoesNotContain("skipped", result, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Loaded", result.Trim());
    }

    [Fact]
    public async Task Execute_WithIncludeAndRootProjects_LoadsUnion()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();
        // include Sample.Unrelated (no deps) + rootProjects App.Domain (pulls Shared.Common)
        var result = (string)await LoadSolutionTool.Execute(manager, store, Slnx(),
            include: new[] { "Sample.*" }, rootProjects: new[] { "App.Domain" });

        // Union closure: Sample.Unrelated (1, no deps) + App.Domain → Shared.Common (2) = 3 loaded.
        // App.Api/App.Infrastructure/App.Api.Tests are NOT pulled (nothing in the seed set references them).
        Assert.Contains("Loaded 3", result);
    }

    [Fact]
    public async Task Execute_FilterMatchingNothing_ThrowsActionableError()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            LoadSolutionTool.Execute(manager, store, Slnx(),
                include: new[] { "DoesNotMatchAnything.*" }, rootProjects: null));
        Assert.Contains("matched 0 projects", ex.Message);
    }
}
