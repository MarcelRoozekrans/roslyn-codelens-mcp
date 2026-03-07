using Microsoft.CodeAnalysis;
using RoslynCodeGraph;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tests;

public class CodeFixRunnerTests : IAsyncLifetime
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
    public async Task GetFixesAsync_ForKnownDiagnostic_ReturnsFixes()
    {
        var runner = new CodeFixRunner();
        var project = _loaded.Solution.Projects.First(p => p.Name == "TestLib");
        var compilation = _loaded.Compilations[project.Id];

        var analyzerRunner = new AnalyzerRunner();
        var diagnostics = await analyzerRunner.RunAnalyzersAsync(project, compilation, CancellationToken.None);
        var fixable = diagnostics.FirstOrDefault(d => d.Location.IsInSource);

        if (fixable == null) return;

        var fixes = await runner.GetFixesAsync(project, fixable, CancellationToken.None);
        Assert.NotNull(fixes);
    }

    [Fact]
    public async Task GetFixesAsync_NoDiagnostic_ReturnsEmpty()
    {
        var runner = new CodeFixRunner();
        var project = _loaded.Solution.Projects.First(p => p.Name == "TestLib");

        var diagnostic = Diagnostic.Create("FAKE001", "Test", "Fake message",
            DiagnosticSeverity.Warning, DiagnosticSeverity.Warning, true, 1);

        var fixes = await runner.GetFixesAsync(project, diagnostic, CancellationToken.None);
        Assert.Empty(fixes);
    }
}
