using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Verifies graceful handling of legacy (non-SDK-style) .NET Framework projects.
/// Regression tests for https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/175
/// </summary>
public class LegacySolutionHandlingTests
{
    private static string LegacySolutionPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "LegacySolution", "Legacy.sln"));

    private static string ValidSolutionPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

    [Fact]
    public async Task SolutionManager_CreateAsync_DoesNotThrowOnLegacyProject()
    {
        var manager = await SolutionManager.CreateAsync(LegacySolutionPath);
        manager.Dispose();
    }

    [Fact]
    public async Task SolutionManager_LegacyProject_PopulatesSkippedList()
    {
        var manager = await SolutionManager.CreateAsync(LegacySolutionPath);
        await manager.WaitForWarmupAsync();

        var loaded = manager.GetLoadedSolution();
        Assert.NotEmpty(loaded.SkippedProjects);
        Assert.Contains(loaded.SkippedProjects, p => string.Equals(p.Kind, "Legacy", StringComparison.Ordinal));
        Assert.Contains(loaded.SkippedProjects, p => p.Reason.Contains("non-SDK", StringComparison.OrdinalIgnoreCase));

        manager.Dispose();
    }

    [Fact]
    public async Task MultiSolutionManager_LegacyOnly_LoadsWithSkippedReported()
    {
        var manager = await MultiSolutionManager.CreateAsync(new[] { LegacySolutionPath });

        var solutions = manager.ListSolutions();
        var solution = Assert.Single(solutions);
        Assert.NotEmpty(solution.SkippedProjects);
        Assert.Contains(solution.SkippedProjects, p => string.Equals(p.Kind, "Legacy", StringComparison.Ordinal));

        manager.Dispose();
    }

    [Fact]
    public async Task MultiSolutionManager_MixedSolutions_LoadsValidAndReportsLegacySkipped()
    {
        var manager = await MultiSolutionManager.CreateAsync(new[] { LegacySolutionPath, ValidSolutionPath });

        var solutions = manager.ListSolutions();
        Assert.Equal(2, solutions.Count);

        var legacy = solutions.Single(s => s.Path.EndsWith("Legacy.sln", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(legacy.SkippedProjects);

        var valid = solutions.Single(s => s.Path.EndsWith("TestSolution.slnx", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(valid.SkippedProjects);
        Assert.Equal("ready", valid.Status);

        manager.Dispose();
    }

    [Fact]
    public async Task LoadSolutionTool_LegacySolution_ReturnsSkippedSummary()
    {
        var manager = MultiSolutionManager.CreateEmpty();

        var result = await LoadSolutionTool.Execute(manager, LegacySolutionPath);

        Assert.Contains("Loaded and activated", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Skipped", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Legacy", result, StringComparison.OrdinalIgnoreCase);

        manager.Dispose();
    }

    [Fact]
    public void ProjectClassifier_DetectsLegacyAndSdkStyle()
    {
        var classified = ProjectClassifier.EnumerateProjects(LegacySolutionPath);
        Assert.NotEmpty(classified);
        Assert.Contains(classified, c => c.Kind == ProjectClassifier.ProjectKind.Legacy);

        var validClassified = ProjectClassifier.EnumerateProjects(ValidSolutionPath);
        Assert.NotEmpty(validClassified);
        Assert.All(validClassified, c => Assert.Equal(ProjectClassifier.ProjectKind.SdkStyle, c.Kind));
    }
}
