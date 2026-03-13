using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"),
     Description("List compiler errors and warnings across the solution, optionally including analyzer diagnostics")]
    public static async Task<IReadOnlyList<DiagnosticInfo>> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum severity: 'error' or 'warning' (default: warning)")] string? severity = null,
        [Description("Include analyzer diagnostics (default: true)")] bool includeAnalyzers = true,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await GetDiagnosticsLogic.ExecuteAsync(manager.GetLoadedSolution(), manager.GetResolver(), project, severity, includeAnalyzers, ct).ConfigureAwait(false);
    }
}
