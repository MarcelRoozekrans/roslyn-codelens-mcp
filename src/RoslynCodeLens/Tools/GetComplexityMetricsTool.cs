using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetComplexityMetricsTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "get_complexity_metrics"),
     Description("Calculate cyclomatic complexity for methods. Returns methods exceeding the threshold. " +
                 "Returns an envelope with items sorted worst-first, totalCount, truncated, limit (default 100), and a summary with max/avg/overThreshold.")]
    public static ToolListResult<ComplexityMetric> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum complexity threshold (default: 10)")] int threshold = 10,
        [Description("Maximum number of items to return (default: 100). Items are sorted by complexity desc (worst first).")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = GetComplexityMetricsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project, threshold);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw, threshold);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<ComplexityMetric> Sort(IReadOnlyList<ComplexityMetric> items)
        => items
            .OrderByDescending(m => m.Complexity)
            .ThenBy(m => m.File, StringComparer.Ordinal)
            .ThenBy(m => m.Line)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<ComplexityMetric> items, int threshold)
    {
        if (items.Count == 0) return new { max = 0, avg = 0.0, overThreshold = 0 };
        var max = items.Max(m => m.Complexity);
        var avg = items.Average(m => m.Complexity);
        var overThreshold = items.Count(m => m.Complexity > threshold);
        return new { max, avg, overThreshold };
    }
}
