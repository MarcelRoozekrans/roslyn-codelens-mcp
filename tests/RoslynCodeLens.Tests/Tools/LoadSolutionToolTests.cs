using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class LoadSolutionToolTests
{
    private readonly string _solutionPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

    [Fact]
    public async Task Execute_FileNotFound_ThrowsFileNotFoundException()
    {
        var manager = MultiSolutionManager.CreateEmpty();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => LoadSolutionTool.Execute(manager, "/does/not/exist.sln"));

        manager.Dispose();
    }

    [Fact]
    public async Task Execute_ValidPath_ReturnsLoadedMessage()
    {
        var manager = MultiSolutionManager.CreateEmpty();

        var result = await LoadSolutionTool.Execute(manager, _solutionPath);

        Assert.Contains("Loaded", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TestSolution", result, StringComparison.OrdinalIgnoreCase);
        manager.Dispose();
    }
}
