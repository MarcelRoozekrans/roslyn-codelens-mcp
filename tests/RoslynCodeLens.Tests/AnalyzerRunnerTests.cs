using Microsoft.CodeAnalysis;
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class AnalyzerRunnerTests : IAsyncLifetime
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
    public async Task RunAnalyzersAsync_ReturnsAnalyzerDiagnostics()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var compilation = _loaded.Compilations[project.Id];

        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("all", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);
        var diagnostics = await AnalyzerRunner.RunAnalyzersAsync(project, compilation, allowlist, CancellationToken.None);

        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Id.StartsWith("CA", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAnalyzersAsync_NoAnalyzers_ReturnsEmpty()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib2", StringComparison.Ordinal));
        var compilation = _loaded.Compilations[project.Id];

        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("all", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);
        var diagnostics = await AnalyzerRunner.RunAnalyzersAsync(project, compilation, allowlist, CancellationToken.None);

        Assert.False(diagnostics.IsDefault);
    }

    [Fact]
    public async Task RunAnalyzersAsync_WithStrictAllowlist_FiltersAllAnalyzers()
    {
        var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
        var compilation = _loaded.Compilations[project.Id];

        // strict policy with impossible NuGet and SDK roots → no analyzer DLL should pass.
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist(
            "strict",
            nugetGlobal: OperatingSystem.IsWindows() ? "Z:\\nope-nuget-root" : "/nope-nuget-root",
            dotnetSdkRoot: "/nope-sdk");
        var diagnostics = await AnalyzerRunner.RunAnalyzersAsync(project, compilation, allowlist, CancellationToken.None);
        Assert.Empty(diagnostics);
    }
}
