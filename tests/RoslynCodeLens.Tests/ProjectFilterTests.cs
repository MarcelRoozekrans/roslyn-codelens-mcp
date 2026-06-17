using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class ProjectFilterTests
{
    [Fact]
    public void HasSeeds_ReturnsFalse_WhenBothEmpty()
    {
        var filter = new ProjectFilter(Array.Empty<string>(), Array.Empty<string>());
        Assert.False(filter.HasSeeds);
    }

    [Fact]
    public void HasSeeds_ReturnsTrue_WhenIncludeNonEmpty()
    {
        var filter = new ProjectFilter(new[] { "App.*" }, Array.Empty<string>());
        Assert.True(filter.HasSeeds);
    }

    [Fact]
    public void HasSeeds_ReturnsTrue_WhenRootProjectsNonEmpty()
    {
        var filter = new ProjectFilter(Array.Empty<string>(), new[] { "App.Api" });
        Assert.True(filter.HasSeeds);
    }
}
