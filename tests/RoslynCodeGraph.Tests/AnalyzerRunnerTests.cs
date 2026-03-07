using Microsoft.CodeAnalysis;
using RoslynCodeGraph;

namespace RoslynCodeGraph.Tests;

public class AnalyzerRunnerTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RunAnalyzersAsync_ReturnsAnalyzerDiagnostics()
    {
        var runner = new AnalyzerRunner();
        var project = _loaded.Solution.Projects.First(p => p.Name == "TestLib");
        var compilation = _loaded.Compilations[project.Id];

        var diagnostics = await runner.RunAnalyzersAsync(project, compilation, CancellationToken.None);

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Id.StartsWith("CA"));
    }

    [Fact]
    public async Task RunAnalyzersAsync_NoAnalyzers_ReturnsEmpty()
    {
        var runner = new AnalyzerRunner();
        var project = _loaded.Solution.Projects.First(p => p.Name == "TestLib2");
        var compilation = _loaded.Compilations[project.Id];

        var diagnostics = await runner.RunAnalyzersAsync(project, compilation, CancellationToken.None);

        Assert.NotNull(diagnostics);
    }
}
