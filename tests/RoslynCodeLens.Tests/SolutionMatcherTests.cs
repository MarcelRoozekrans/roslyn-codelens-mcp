using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class SolutionMatcherTests
{
    private static readonly string A = Path.GetFullPath("/tmp/x/A.sln");
    private static readonly string Data = Path.GetFullPath("/tmp/y/DATA.sln");
    private static readonly string Web = Path.GetFullPath("/tmp/z/Web.slnx");
    private static readonly string[] Keys = [A, Data, Web];

    [Fact]
    public void Match_FullPath_WinsWhereSubstringIsAmbiguous()
    {
        // The first key's path is a substring of the second, so substring matching alone is ambiguous.
        var nested = Path.GetFullPath("/tmp/b/tmp/x/A.sln");
        Assert.Equal(A, SolutionMatcher.Match([A, nested], A));
    }

    [Fact]
    public void Match_FullPathWithDotSegment_Matches() =>
        // Built from the key so the name is fully qualified on every platform, a drive included on Windows.
        Assert.Equal(A, SolutionMatcher.Match(Keys, Path.Combine(Path.GetDirectoryName(A)!, ".", "A.sln")));

    [Fact]
    public void Match_RelativeFileName_SharedByTwoSolutions_IsAmbiguous()
    {
        // A bare file name must not be read as a path under the server's working directory: with
        // one worktree per agent, that would silently pick the checkout the server started in.
        var underCwd = Path.GetFullPath("App.sln");
        var worktree = Path.GetFullPath("/tmp/wt-2/App.sln");

        var ex = Assert.Throws<McpToolException>(() => SolutionMatcher.Match([underCwd, worktree], "App.sln"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
    }

    [Fact]
    public void Match_FullPathDifferentCase_Matches() =>
        Assert.Equal(A, SolutionMatcher.Match(Keys, A.ToUpperInvariant()));

    [Fact]
    public void Match_FileName_WinsOverSubstring() =>
        Assert.Equal(A, SolutionMatcher.Match(Keys, "a.sln"));

    [Fact]
    public void Match_Substring_UniqueMatch() =>
        Assert.Equal(Web, SolutionMatcher.Match(Keys, "Web"));

    [Fact]
    public void Match_Substring_Ambiguous_Throws()
    {
        var ex = Assert.Throws<McpToolException>(() => SolutionMatcher.Match(Keys, ".sln"));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
    }

    [Fact]
    public void Match_NoMatch_Throws()
    {
        var ex = Assert.Throws<McpToolException>(() => SolutionMatcher.Match(Keys, "Nope"));
        Assert.Equal(ToolErrorCode.ProjectNotFound, ex.Code);
    }

    [Fact]
    public void Match_DriveRelativePath_SharedFileName_IsAmbiguousOnWindows()
    {
        // `\repo\App.sln` is rooted but not fully qualified on Windows: it depends on the current drive,
        // so it must not pick C:\repo\App.sln over D:\repo\App.sln. Off Windows there are no drives and
        // the case does not exist. The Windows CI job runs this class.
        if (!OperatingSystem.IsWindows())
            return;

        var ex = Assert.Throws<McpToolException>(() =>
            SolutionMatcher.Match([@"C:\repo\App.sln", @"D:\repo\App.sln"], @"\repo\App.sln"));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
    }

    [Fact]
    public void Match_PathWithInvalidCharacter_IsNotFound()
    {
        // GetFullPath rejects the NUL character; the name then simply matches no tier.
        var ex = Assert.Throws<McpToolException>(() =>
            SolutionMatcher.Match(Keys, Path.Combine(Path.GetDirectoryName(A)!, "A\0.sln")));
        Assert.Equal(ToolErrorCode.ProjectNotFound, ex.Code);
    }
}
