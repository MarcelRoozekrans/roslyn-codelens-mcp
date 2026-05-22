using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetDiagnosticsTool
{
    private const int DefaultLimit = 1000;

    [McpServerTool(Name = "get_diagnostics"),
     Description("List compiler errors and warnings across the solution, optionally including analyzer diagnostics. " +
                 "Analyzer diagnostics require the solution to be trusted (see 'trust_solution'). " +
                 "Returns an envelope with items, totalCount, truncated, limit, and a severity summary.")]
    public static async Task<ToolListResult<DiagnosticInfo>> Execute(
        MultiSolutionManager manager,
        Security.TrustStore trustStore,
        Security.AnalyzerAllowlist allowlist,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum severity: 'error' or 'warning' (default: warning)")] string? severity = null,
        [Description("Include analyzer diagnostics (default: false — requires trust_solution to be called first)")]
            bool includeAnalyzers = false,
        [Description("Maximum number of items to return (default: 1000). Items are sorted severity-desc, then file, then line.")]
            int? limit = null,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var raw = await GetDiagnosticsLogic.ExecuteAsync(
            manager.GetLoadedSolution(), manager.GetResolver(),
            project, severity, includeAnalyzers, trustStore, allowlist, ct).ConfigureAwait(false);

        // Sort severity-first so truncated top-N keeps the most important diagnostics.
        var sorted = SortBySeverityFileLine(raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<DiagnosticInfo> SortBySeverityFileLine(IReadOnlyList<DiagnosticInfo> items)
    {
        return items
            .OrderBy(d => SeverityRank(d.Severity))
            .ThenBy(d => d.File, StringComparer.Ordinal)
            .ThenBy(d => d.Line)
            .ToList();
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Error" => 0,
        "Warning" => 1,
        "Info" => 2,
        "Hidden" => 3,
        _ => 4,
    };

    internal static object BuildSummary(IReadOnlyList<DiagnosticInfo> items)
    {
        var error = 0;
        var warning = 0;
        var info = 0;
        var hidden = 0;
        foreach (var d in items)
        {
            switch (d.Severity)
            {
                case "Error": error++; break;
                case "Warning": warning++; break;
                case "Info": info++; break;
                case "Hidden": hidden++; break;
            }
        }
        return new { error, warning, info, hidden };
    }
}
