using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class ProjectGraphReaderTests
{
    private static string FixturePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "GraphReader", fileName);

    [Fact]
    public void ReadProjectReferences_ReturnsAbsolutePaths()
    {
        var refs = ProjectGraphReader.ReadProjectReferences(FixturePath("ProjectWithRefs.csproj"));

        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.True(Path.IsPathRooted(r)));
        Assert.Contains(refs, r => r.EndsWith("App.Domain.csproj"));
        Assert.Contains(refs, r => r.EndsWith("Shared.Common.csproj"));
    }

    [Fact]
    public void ReadProjectReferences_NormalisesBackslashSeparators_ToCanonicalPath()
    {
        // The fixture references "..\App.Domain\App.Domain.csproj" with BACKSLASHES
        // (the form MSBuild emits on Windows). On non-Windows platforms backslash is
        // an ordinary filename character, so without separator normalisation the ".."
        // segment is not collapsed and literal backslashes survive — producing a path
        // that matches no project in the solution and silently drops the graph edge.
        // Assert against the OS-native canonical path so this is a real cross-platform
        // contract, not just an EndsWith check that the garbage path would also satisfy.
        var projectPath = FixturePath("ProjectWithRefs.csproj");
        var dir = Path.GetDirectoryName(projectPath)!;
        var refs = ProjectGraphReader.ReadProjectReferences(projectPath);

        var expectedDomain = Path.GetFullPath(Path.Combine(dir, "..", "App.Domain", "App.Domain.csproj"));
        var expectedShared = Path.GetFullPath(Path.Combine(dir, "..", "Shared.Common", "Shared.Common.csproj"));

        Assert.Contains(expectedDomain, refs);
        Assert.Contains(expectedShared, refs);
    }

    [Fact]
    public void ReadProjectReferences_ReturnsEmpty_WhenNoRefs()
    {
        var refs = ProjectGraphReader.ReadProjectReferences(FixturePath("Sdk_NoRefs.csproj"));
        Assert.Empty(refs);
    }

    [Fact]
    public void ReadProjectReferences_ReturnsEmpty_WhenFileMalformed()
    {
        var refs = ProjectGraphReader.ReadProjectReferences(FixturePath("Malformed.csproj"));
        Assert.Empty(refs);
    }
}
