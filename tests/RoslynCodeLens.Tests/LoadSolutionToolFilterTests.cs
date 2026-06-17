using RoslynCodeLens;
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
        var result = await LoadSolutionTool.Execute(manager, Slnx(),
            include: new[] { "App.*" }, rootProjects: null);

        Assert.Contains("Loaded 5", result);
        Assert.Contains("skipped 1", result);
    }

    [Fact]
    public async Task Execute_NoFilter_BehavesAsBefore()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        var result = await LoadSolutionTool.Execute(manager, Slnx(),
            include: null, rootProjects: null);

        Assert.Contains("Loaded", result);
        Assert.DoesNotContain("skipped", result, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Loaded", result.Trim());
    }
}
