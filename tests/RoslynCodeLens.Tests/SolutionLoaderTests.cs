using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

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

    [Fact]
    public async Task OpenAsync_ReturnsSolutionWithoutCompilations()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx");
        fixturePath = Path.GetFullPath(fixturePath);

        var loader = new SolutionLoader();
        var (solution, workspace, _) = await loader.OpenAsync(fixturePath);

        Assert.NotNull(solution);
        Assert.True(solution.Projects.Count() >= 2);
    }

    [Fact]
    public async Task CompileAllParallelAsync_CompilesAllProjects()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx");
        fixturePath = Path.GetFullPath(fixturePath);

        var loader = new SolutionLoader();
        var (solution, workspace, _) = await loader.OpenAsync(fixturePath);
        var compilations = await loader.CompileAllParallelAsync(solution);

        Assert.True(compilations.Count >= 2);
        Assert.All(compilations.Values, c => Assert.NotNull(c));
    }
}
