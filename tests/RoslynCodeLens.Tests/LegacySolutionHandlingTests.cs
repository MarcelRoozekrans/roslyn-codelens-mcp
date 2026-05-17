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

        Assert.True(manager.HasLoadFailure);
        Assert.NotNull(manager.LoadFailureMessage);
        manager.Dispose();
    }

    [Fact]
    public async Task SolutionManager_FailedLoad_SurfacesFriendlyErrorOnAccess()
    {
        var manager = await SolutionManager.CreateAsync(LegacySolutionPath);

        var ex = Assert.Throws<InvalidOperationException>(() => manager.GetLoadedSolution());
        Assert.Contains("Failed to load solution", ex.Message, StringComparison.OrdinalIgnoreCase);
        manager.Dispose();
    }

    [Fact]
    public async Task SolutionManager_FailedLoad_FriendlyMessageMentionsLegacyProjects()
    {
        var manager = await SolutionManager.CreateAsync(LegacySolutionPath);

        Assert.NotNull(manager.LoadFailureMessage);
        Assert.Contains("non-SDK-style", manager.LoadFailureMessage!, StringComparison.OrdinalIgnoreCase);
        manager.Dispose();
    }

    [Fact]
    public async Task MultiSolutionManager_CreateAsync_TolerateLegacySolutionAlongsideValidOne()
    {
        var manager = await MultiSolutionManager.CreateAsync(new[] { LegacySolutionPath, ValidSolutionPath });

        var solutions = manager.ListSolutions();
        Assert.Equal(2, solutions.Count);
        Assert.Contains(solutions, s => s.Status == "error");
        Assert.Contains(solutions, s => s.Status == "ready" || s.Status == "empty");

        var active = solutions.Single(s => s.IsActive);
        Assert.NotEqual("error", active.Status);

        manager.Dispose();
    }

    [Fact]
    public async Task MultiSolutionManager_CreateAsync_OnlyLegacySolution_DoesNotThrow()
    {
        var manager = await MultiSolutionManager.CreateAsync(new[] { LegacySolutionPath });

        var solutions = manager.ListSolutions();
        Assert.Single(solutions);
        Assert.Equal("error", solutions[0].Status);

        manager.Dispose();
    }

    [Fact]
    public async Task LoadSolutionTool_LegacySolution_ReturnsFriendlyError()
    {
        var manager = MultiSolutionManager.CreateEmpty();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LoadSolutionTool.Execute(manager, LegacySolutionPath));

        Assert.Contains("Failed to load solution", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The failed solution must not have been retained.
        Assert.Empty(manager.ListSolutions());
        manager.Dispose();
    }
}
