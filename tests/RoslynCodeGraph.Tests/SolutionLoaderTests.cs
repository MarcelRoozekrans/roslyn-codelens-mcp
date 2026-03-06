using RoslynCodeGraph;

namespace RoslynCodeGraph.Tests;

public class SolutionLoaderTests
{
    [Fact]
    public async Task LoadSolution_ReturnsCompiledSolution()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx");
        fixturePath = Path.GetFullPath(fixturePath);

        var loader = new SolutionLoader();
        var result = await loader.LoadAsync(fixturePath);

        Assert.NotNull(result.Solution);
        Assert.True(result.Compilations.Count >= 2);
    }
}
