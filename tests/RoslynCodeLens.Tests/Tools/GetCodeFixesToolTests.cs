using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetCodeFixesToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GetCodeFixesToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public async Task GetCodeFixes_NoMatchingDiagnostic_ReturnsEmpty()
    {
        var trustStore = new RoslynCodeLens.Security.TrustStore(Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json"));
        trustStore.AddSessionTrust(_loaded.Solution.FilePath!);
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("all", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

        var results = await GetCodeFixesLogic.ExecuteAsync(
            _loaded, _resolver, "FAKE999", "NonExistent.cs", 1, trustStore, allowlist, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetCodeFixes_ReturnsSuggestions()
    {
        var trustStore = new RoslynCodeLens.Security.TrustStore(Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json"));
        trustStore.AddSessionTrust(_loaded.Solution.FilePath!);
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("all", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

        var diagnostics = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
        var diag = diagnostics.FirstOrDefault(d => !string.IsNullOrEmpty(d.File));

        if (diag == null) return;

        var results = await GetCodeFixesLogic.ExecuteAsync(
            _loaded, _resolver, diag.Id, diag.File, diag.Line, trustStore, allowlist, CancellationToken.None);
        Assert.NotNull(results);
    }

    [Fact]
    public async Task GetCodeFixes_UntrustedSolution_ThrowsSolutionNotTrusted()
    {
        // No trust added — but a diagnostic must actually exist at the call site,
        // otherwise the early "no matching diagnostics" return short-circuits the trust check.
        var trustStore = new RoslynCodeLens.Security.TrustStore(Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json"));
        var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("all", RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

        // Find an existing compiler diagnostic to ensure we reach the trust check.
        var diagnostics = GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
        var diag = diagnostics.FirstOrDefault(d => !string.IsNullOrEmpty(d.File));
        if (diag is null) return; // no diagnostics in fixture — skip

        var ex = await Assert.ThrowsAsync<McpToolException>(async () =>
        {
            await GetCodeFixesLogic.ExecuteAsync(
                _loaded, _resolver, diag.Id, diag.File, diag.Line, trustStore, allowlist, CancellationToken.None);
        });
        Assert.Equal(ToolErrorCode.SolutionNotTrusted, ex.Code);
    }
}
