using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class MultiSolutionManagerTests : IAsyncLifetime
{
    private string _solutionPath = null!;

    public Task InitializeAsync()
    {
        _solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
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
        Assert.Throws<InvalidOperationException>((Action)(() => multi.EnsureLoaded()));
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
        Assert.Throws<InvalidOperationException>(() => multi.SetActiveSolution("DoesNotExist"));
        multi.Dispose();
    }
}
