using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"),
     Description("List compiler errors and warnings across the solution, optionally including analyzer diagnostics. " +
                 "Analyzer diagnostics require the solution to be trusted (see 'trust_solution').")]
    public static async Task<IReadOnlyList<DiagnosticInfo>> Execute(
        MultiSolutionManager manager,
        Security.TrustStore trustStore,
        Security.AnalyzerAllowlist allowlist,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum severity: 'error' or 'warning' (default: warning)")] string? severity = null,
        [Description("Include analyzer diagnostics (default: false — requires trust_solution to be called first)")]
            bool includeAnalyzers = false,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await GetDiagnosticsLogic.ExecuteAsync(manager.GetLoadedSolution(), manager.GetResolver(), project, severity, includeAnalyzers, trustStore, allowlist, ct).ConfigureAwait(false);
    }
}
