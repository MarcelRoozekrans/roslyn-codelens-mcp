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
            new Dictionary<string, SolutionManager> { [AKey] = a, [BKey] = b }, activeKey);
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
}
