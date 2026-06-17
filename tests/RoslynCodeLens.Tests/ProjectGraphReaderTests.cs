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
