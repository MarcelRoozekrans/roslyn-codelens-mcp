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
    public void Match_FullPath_WinsOverSubstring() =>
        Assert.Equal(A, SolutionMatcher.Match(Keys, A));

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
}
