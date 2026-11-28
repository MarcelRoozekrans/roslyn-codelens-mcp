using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class MultiSolutionManagerResolveTests
{
    private static readonly string AKey = Path.GetFullPath("/tmp/a/A.sln");
    private static readonly string BKey = Path.GetFullPath("/tmp/b/B.sln");

    private static (MultiSolutionManager Multi, SolutionManager A, SolutionManager B) Create(string? activeKey)
    {
        var a = SolutionManager.CreateEmpty();
        var b = SolutionManager.CreateEmpty();
        var multi = MultiSolutionManager.CreateForTesting(
            new Dictionary<string, SolutionManager>(StringComparer.Ordinal) { [AKey] = a, [BKey] = b }, activeKey);
        return (multi, a, b);
    }

    [Fact]
    public void Resolve_Null_ReturnsActive()
    {
        var (multi, a, _) = Create(AKey);
        Assert.Same(a, multi.Resolve(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyString_ReturnsActive(string solution)
    {
        var (multi, a, _) = Create(AKey);
        Assert.Same(a, multi.Resolve(solution));
    }

    [Fact]
    public void Resolve_Name_ReturnsNamedWithoutChangingActive()
    {
        var (multi, _, b) = Create(AKey);
        Assert.Same(b, multi.Resolve("B"));
        Assert.Equal(AKey, multi.ListSolutions().Single(s => s.IsActive).Path);
    }

    [Fact]
    public void Resolve_Unknown_ThrowsProjectNotFound()
    {
        var (multi, _, _) = Create(AKey);
        var ex = Assert.Throws<McpToolException>(() => multi.Resolve("Nope"));
        Assert.Equal(ToolErrorCode.ProjectNotFound, ex.Code);
    }

    [Fact]
    public void Resolve_NoActive_Null_ThrowsInvalidArgument()
    {
        var (multi, _, _) = Create(null);
        var ex = Assert.Throws<McpToolException>(() => multi.Resolve(null));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void NextActiveKey_ActivateTrue_ReturnsLoaded() =>
        Assert.Equal(BKey, MultiSolutionManager.NextActiveKey(AKey, BKey, activate: true));

    [Fact]
    public void NextActiveKey_ActivateFalse_KeepsCurrent() =>
        Assert.Equal(AKey, MultiSolutionManager.NextActiveKey(AKey, BKey, activate: false));

    [Fact]
    public void NextActiveKey_ActivateFalseNoCurrent_ReturnsLoaded() =>
        Assert.Equal(BKey, MultiSolutionManager.NextActiveKey(null, BKey, activate: false));

    [Fact]
    public async Task LoadSolutionAsync_FailedFilteredReload_LeavesNoDanglingActiveKey()
    {
        // A filtered re-load drops the old workspace before opening the new one. If the new one fails,
        // the active key must not keep pointing at a solution that is no longer loaded.
        var dir = Directory.CreateTempSubdirectory("codelens-606-");
        try
        {
            var bad = Path.Combine(dir.FullName, "Bad.sln");
            await File.WriteAllTextAsync(bad, "not a solution file");
            using var multi = MultiSolutionManager.CreateForTesting(
                new Dictionary<string, SolutionManager>(StringComparer.Ordinal) { [bad] = SolutionManager.CreateEmpty() }, bad);

            await Assert.ThrowsAsync<McpToolException>(() =>
                multi.LoadSolutionAsync(bad, new ProjectFilter(["Anything"], []), activate: false));

            var ex = Assert.Throws<McpToolException>(() => multi.Resolve(null));
            Assert.StartsWith("No solution loaded", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetSkippedProjects_UnknownSolution_ReturnsEmpty()
    {
        var (multi, _, _) = Create(AKey);
        Assert.Empty(multi.GetSkippedProjects("Nope"));
    }

    [Fact]
    public void GetLoadDiagnostics_UnknownSolution_ReturnsEmpty()
    {
        var (multi, _, _) = Create(AKey);
        Assert.Empty(multi.GetLoadDiagnostics("Nope"));
    }

    [Fact]
    public void GetSkippedProjects_NamedSolution_ReadsThatSolution()
    {
        var (multi, _, _) = Create(AKey);
        Assert.Empty(multi.GetSkippedProjects("B"));
        Assert.Empty(multi.GetLoadDiagnostics("B"));
    }
}
