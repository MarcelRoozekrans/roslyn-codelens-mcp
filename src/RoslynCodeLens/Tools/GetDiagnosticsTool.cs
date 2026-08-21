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
        var context = manager.GetAnalysisContext();
        var raw = await GetDiagnosticsLogic.ExecuteAsync(
            context.Loaded, context.Resolver,
            project, severity, includeAnalyzers, trustStore, allowlist, ct).ConfigureAwait(false);

        // Sort severity-first so truncated top-N keeps the most important diagnostics.
        var sorted = SortBySeverityFileLine(raw);
        var summary = BuildSummary(raw, context.Loaded.LoadDiagnostics);
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

    /// <summary>
    /// Severity tallies, plus an explicit <c>unreliable</c> block when the solution loaded
    /// degraded. get_diagnostics is the tool where a degraded load does the most damage: it
    /// reports phantom errors with the same confidence as real ones, and an agent acting on that
    /// output "fixes" code that was never broken (issue #399). Callers must be able to see that
    /// from the response itself rather than having to call load_solution separately.
    /// </summary>
    internal static object BuildSummary(
        IReadOnlyList<DiagnosticInfo> items,
        IReadOnlyList<string>? loadDiagnostics = null)
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
        if (loadDiagnostics is null || loadDiagnostics.Count == 0)
            return new { error, warning, info, hidden };

        return new
        {
            error,
            warning,
            info,
            hidden,
            unreliable = new
            {
                reason = "The solution loaded degraded, so these diagnostics may include errors that "
                       + "do not exist in a real build. Verify against 'dotnet build' before acting on them.",
                loadDiagnostics,
            },
        };
    }
}
