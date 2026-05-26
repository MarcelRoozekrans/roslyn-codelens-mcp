using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class MultiSolutionManagerTests : IAsyncLifetime
{
    private string _solutionPath = null!;
    private string _altSolutionPath = null!;

    public Task InitializeAsync()
    {
        _solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _altSolutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolutionAlt", "TestSolutionAlt.slnx"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateAsync_SinglePath_DelegatesEnsureLoaded()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        multi.EnsureLoaded(); // must not throw
        multi.Dispose();
    }

    [Fact]
    public async Task GetLoadedSolution_ReturnsSolution()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        await multi.WaitForWarmupAsync();
        Assert.False(multi.GetLoadedSolution().IsEmpty);
        multi.Dispose();
    }

    [Fact]
    public async Task GetResolver_ReturnsResolver()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        await multi.WaitForWarmupAsync();
        Assert.NotNull(multi.GetResolver());
        multi.Dispose();
    }

    [Fact]
    public void CreateEmpty_EnsureLoaded_Throws()
    {
        var multi = MultiSolutionManager.CreateEmpty();
        var ex = Assert.Throws<McpToolException>((Action)(() => multi.EnsureLoaded()));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        multi.Dispose();
    }

    [Fact]
    public async Task CreateAsync_DuplicatePaths_DoesNotThrow()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath, _solutionPath]);
        multi.EnsureLoaded();
        multi.Dispose();
    }

    [Fact]
    public async Task ListSolutions_SinglePath_ReturnsSingleActiveEntry()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        var list = multi.ListSolutions();
        Assert.Single(list);
        Assert.True(list[0].IsActive);
        Assert.Equal(Path.GetFullPath(_solutionPath), list[0].Path, StringComparer.OrdinalIgnoreCase);
        multi.Dispose();
    }

    [Fact]
    public async Task ListSolutions_TwoLoaded_OnlyActiveIsMarked()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath, _altSolutionPath]);
        multi.SetActiveSolution("TestSolutionAlt");

        var list = multi.ListSolutions();

        Assert.Equal(2, list.Count);
        Assert.Single(list, s => s.IsActive);
        Assert.Contains(list, s => s.IsActive && s.Path.Contains("TestSolutionAlt", StringComparison.OrdinalIgnoreCase));
        multi.Dispose();
    }

    [Fact]
    public async Task SetActiveSolution_ByPartialName_SwitchesActive()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        var switched = multi.SetActiveSolution("TestSolution");
        Assert.Contains("TestSolution", switched, StringComparison.OrdinalIgnoreCase);
        multi.Dispose();
    }

    [Fact]
    public async Task SetActiveSolution_UnknownName_Throws()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        var ex = Assert.Throws<McpToolException>(() => multi.SetActiveSolution("DoesNotExist"));
        Assert.Equal(ToolErrorCode.ProjectNotFound, ex.Code);
        multi.Dispose();
    }

    [Fact]
    public async Task SetActiveSolution_AmbiguousName_Throws()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath, _altSolutionPath]);

        var ex = Assert.Throws<McpToolException>(() => multi.SetActiveSolution("TestSolution"));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Contains("Ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        multi.Dispose();
    }

    [Fact]
    public async Task ForceReloadAsync_ReturnsPositiveProjectCount()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        await multi.WaitForWarmupAsync();
        var (projectCount, elapsed) = await multi.ForceReloadAsync();
        Assert.True(projectCount > 0);
        Assert.True(elapsed > TimeSpan.Zero);
        multi.Dispose();
    }

    // --- LoadSolutionAsync tests ---

    [Fact]
    public async Task LoadSolutionAsync_NewSolution_BecomesActive()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);

        await multi.LoadSolutionAsync(_altSolutionPath);

        var list = multi.ListSolutions();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.IsActive && s.Path.Contains("TestSolutionAlt", StringComparison.OrdinalIgnoreCase));
        multi.Dispose();
    }

    [Fact]
    public async Task LoadSolutionAsync_FromEmpty_LoadsAndActivates()
    {
        var multi = MultiSolutionManager.CreateEmpty();

        var loaded = await multi.LoadSolutionAsync(_solutionPath);

        Assert.Contains("TestSolution", loaded, StringComparison.OrdinalIgnoreCase);
        multi.EnsureLoaded(); // must not throw — solution is active
        var list = multi.ListSolutions();
        Assert.Single(list);
        Assert.True(list[0].IsActive);
        multi.Dispose();
    }

    [Fact]
    public async Task LoadSolutionAsync_AlreadyLoaded_JustActivates()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        await multi.WaitForWarmupAsync();

        // Load the same solution again — should not throw, just activate
        var loaded = await multi.LoadSolutionAsync(_solutionPath);

        Assert.Contains("TestSolution", loaded, StringComparison.OrdinalIgnoreCase);
        var list = multi.ListSolutions();
        Assert.Single(list); // no duplicate
        multi.Dispose();
    }

    [Fact]
    public async Task LoadSolutionAsync_MakesToolsWork()
    {
        var multi = MultiSolutionManager.CreateEmpty();
        await multi.LoadSolutionAsync(_solutionPath);
        await multi.WaitForWarmupAsync();

        // The resolver should work against the dynamically loaded solution
        var resolver = multi.GetResolver();
        Assert.NotNull(resolver);

        var solution = multi.GetLoadedSolution();
        Assert.False(solution.IsEmpty);
        Assert.True(solution.Compilations.Count > 0);
        multi.Dispose();
    }

    // --- UnloadSolution tests ---

    [Fact]
    public async Task UnloadSolution_RemovesSolutionAndFreesSlot()
    {
        var multi = MultiSolutionManager.CreateEmpty();
        await multi.LoadSolutionAsync(_solutionPath);

        var unloaded = multi.UnloadSolution("TestSolution");

        Assert.Contains("TestSolution", unloaded, StringComparison.OrdinalIgnoreCase);
        var list = multi.ListSolutions();
        Assert.Empty(list);
        multi.Dispose();
    }

    [Fact]
    public async Task UnloadSolution_OnlyOneSolution_LeavesNoActive()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);

        multi.UnloadSolution("TestSolution");
        var list = multi.ListSolutions();
        Assert.Empty(list);
        multi.Dispose();
    }

    [Fact]
    public async Task UnloadSolution_ActiveOfTwo_SwitchesToOther()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath, _altSolutionPath]);
        multi.SetActiveSolution("TestSolutionAlt");

        multi.UnloadSolution("TestSolutionAlt");

        var list = multi.ListSolutions();
        Assert.Single(list);
        Assert.True(list[0].IsActive);
        Assert.Contains("TestSolution", list[0].Path, StringComparison.OrdinalIgnoreCase);
        multi.Dispose();
    }

    [Fact]
    public void UnloadSolution_UnknownName_Throws()
    {
        var multi = MultiSolutionManager.CreateEmpty();
        var ex = Assert.Throws<McpToolException>(() => multi.UnloadSolution("DoesNotExist"));
        Assert.Equal(ToolErrorCode.ProjectNotFound, ex.Code);
        multi.Dispose();
    }

    [Fact]
    public async Task UnloadSolution_AmbiguousName_Throws()
    {
        // Both paths contain "TestSolution" so searching for it matches two solutions.
        var multi = MultiSolutionManager.CreateEmpty();
        await multi.LoadSolutionAsync(_solutionPath);
        await multi.LoadSolutionAsync(_altSolutionPath);

        var ex = Assert.Throws<McpToolException>(() => multi.UnloadSolution("TestSolution"));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Contains("Ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
        multi.Dispose();
    }

    [Fact]
    public async Task LoadThenUnloadThenLoad_WorksCleanly()
    {
        var multi = MultiSolutionManager.CreateEmpty();

        // Load
        await multi.LoadSolutionAsync(_solutionPath);
        Assert.Single(multi.ListSolutions());

        // Unload
        multi.UnloadSolution("TestSolution");
        Assert.Empty(multi.ListSolutions());

        // Load again
        await multi.LoadSolutionAsync(_solutionPath);
        Assert.Single(multi.ListSolutions());
        multi.EnsureLoaded(); // must not throw
        multi.Dispose();
    }
}
