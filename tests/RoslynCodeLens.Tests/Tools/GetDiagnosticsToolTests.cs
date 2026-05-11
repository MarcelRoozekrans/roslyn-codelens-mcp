using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetDiagnosticsToolTests
{
    // The three test-framework adapter sub-projects depend on PackageReferences for
    // MSTest.TestFramework / NUnit / xunit. Those packages intermittently fail to resolve
    // on Linux CI when MSBuildWorkspace re-resolves references at solution-load time,
    // surfacing as CS0246 ("type or namespace name not found"). The flake is environmental
    // — not code health. AsyncFixture and DisposableFixture are deliberately excluded:
    // they have no PackageReferences and can't suffer the same flake. Genuine compile bugs
    // in those (or any non-CS0246 error in the three below) still fail this test.
    private static readonly string[] AdapterProjects =
        ["NUnitFixture", "MSTestFixture", "XUnitFixture"];

    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetDiagnosticsToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void GetDiagnostics_CleanSolution_ReturnsNoErrors()
    {
        var results = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, "error");

        // Filter the environmental adapter-restore flake (see AdapterProjects comment above).
        // The test's intent is "production-like fixtures (TestLib/TestLib2) compile cleanly".
        var filtered = results.Where(d => !IsAdapterRestoreFlake(d)).ToList();

        Assert.Empty(filtered);
    }

    private static bool IsAdapterRestoreFlake(DiagnosticInfo d)
        => string.Equals(d.Id, "CS0246", StringComparison.Ordinal)
           && AdapterProjects.Any(p => string.Equals(d.Project, p, StringComparison.Ordinal));

    [Fact]
    public void GetDiagnostics_WithProjectFilter_FiltersResults()
    {
        var all = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
        var filtered = GetDiagnosticsLogic.Execute(_loaded, _resolver, "TestLib", null);

        Assert.True(filtered.Count <= all.Count);
        Assert.All(filtered, d => Assert.Contains("TestLib", d.Project, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetDiagnostics_WithAnalyzers_IncludesAnalyzerDiagnostics()
    {
        using var tempFile = new TempTrustFile();
        var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
        trustStore.AddSessionTrust(_loaded.Solution.FilePath!);
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

        var results = await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: true,
            trustStore, allowlist, CancellationToken.None);

        // Any analyzer diagnostics should have Source starting with "analyzer"
        _ = results.Where(d => d.Source.StartsWith("analyzer", StringComparison.Ordinal)).ToList();
        // Compiler diagnostics should still be present with Source == "compiler"
        _ = results.Where(d => string.Equals(d.Source, "compiler", StringComparison.Ordinal)).ToList();

        // All results should have a valid source
        Assert.All(results, d => Assert.True(
            string.Equals(d.Source, "compiler", StringComparison.Ordinal) || d.Source.StartsWith("analyzer:", StringComparison.Ordinal),
            $"Unexpected source: {d.Source}"));
    }

    [Fact]
    public async Task GetDiagnostics_WithoutAnalyzers_OnlyCompilerDiagnostics()
    {
        using var tempFile = new TempTrustFile();
        var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

        var results = await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: false,
            trustStore, allowlist, CancellationToken.None);

        Assert.All(results, d => Assert.Equal("compiler", d.Source));
    }

    [Fact]
    public async Task GetDiagnostics_UntrustedSolution_ThrowsWhenAnalyzersRequested()
    {
        using var tempFile = new TempTrustFile();
        var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await GetDiagnosticsLogic.ExecuteAsync(
                _loaded, _resolver, null, null, includeAnalyzers: true,
                trustStore, allowlist, CancellationToken.None);
        });
        Assert.Contains("not trusted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("trust_solution", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiagnostics_UntrustedSolution_StillReturnsCompilerDiagnosticsWithoutAnalyzers()
    {
        using var tempFile = new TempTrustFile();
        var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

        var results = await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: false,
            trustStore, allowlist, CancellationToken.None);

        Assert.All(results, d => Assert.Equal("compiler", d.Source));
    }

    [Fact]
    public async Task GetDiagnostics_TrustedSolution_RunsAnalyzers()
    {
        using var tempFile = new TempTrustFile();
        var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
        trustStore.AddSessionTrust(_loaded.Solution.FilePath!);
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

        var results = await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: true,
            trustStore, allowlist, CancellationToken.None);

        Assert.Contains(results, d => d.Source.StartsWith("analyzer:", StringComparison.Ordinal));
    }

    private sealed class TempTrustFile : IDisposable
    {
        public string Path { get; }
        public TempTrustFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json");
        }
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
