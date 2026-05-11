using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class CodeFixRunnerTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetFixesAsync_ForKnownDiagnostic_ReturnsFixes()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var compilation = _loaded.Compilations[project.Id];

        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("all", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);
        var diagnostics = await AnalyzerRunner.RunAnalyzersAsync(project, compilation, allowlist, CancellationToken.None);
        var fixable = diagnostics.FirstOrDefault(d => d.Location.IsInSource);

        if (fixable == null) return;

        var fixes = await CodeFixRunner.GetFixesAsync(project, fixable, CancellationToken.None);
        Assert.NotNull(fixes);
    }

    [Fact]
    public async Task GetFixesAsync_NoDiagnostic_ReturnsEmpty()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));

        var diagnostic = Diagnostic.Create("FAKE001", "Test", "Fake message",
            DiagnosticSeverity.Warning, DiagnosticSeverity.Warning, true, 1);

        var fixes = await CodeFixRunner.GetFixesAsync(project, diagnostic, CancellationToken.None);
        Assert.Empty(fixes);
    }
}
