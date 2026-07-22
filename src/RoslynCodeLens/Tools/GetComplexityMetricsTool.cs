using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetComplexityMetricsTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "get_complexity_metrics"),
     Description("Calculate complexity for members (methods, constructors, properties, indexers, operators). " +
                 "Reports both 'complexity' (cyclomatic - the number of paths, starts at 1) and 'cognitive' " +
                 "(how hard the code is to follow, starts at 0 - a 0 is not a bug), plus 'maxNesting'. " +
                 "The 'metric' parameter selects which of the two the threshold filters on and the sort uses. " +
                 "Returns an envelope with items sorted worst-first, totalCount, truncated, limit (default 100), " +
                 "and a summary with max/avg/overThreshold plus maxCognitive.")]
    public static ToolListResult<ComplexityMetric> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum complexity threshold, applied to the selected metric (default: 10)")] int threshold = 10,
        [Description("Which metric drives the threshold and the sort: 'cyclomatic' (default) or 'cognitive'. Both are always reported.")]
            string metric = "cyclomatic",
        [Description("Maximum number of items to return (default: 100). Items are sorted by the selected metric desc (worst first).")]
            int? limit = null)
    {
        var kind = ParseMetric(metric);

        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        var raw = GetComplexityMetricsLogic.Execute(context.Loaded, context.Resolver, project, threshold, kind);

        var sorted = Sort(raw, kind);
        var summary = BuildSummary(raw, threshold, kind);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static ComplexityMetricKind ParseMetric(string metric) => metric.ToLowerInvariant() switch
    {
        "cyclomatic" => ComplexityMetricKind.Cyclomatic,
        "cognitive" => ComplexityMetricKind.Cognitive,
        _ => throw new ArgumentException(
            $"Unknown metric '{metric}'. Expected 'cyclomatic' or 'cognitive'.", nameof(metric)),
    };

    internal static IReadOnlyList<ComplexityMetric> Sort(
        IReadOnlyList<ComplexityMetric> items,
        ComplexityMetricKind metric = ComplexityMetricKind.Cyclomatic)
        => items
            .OrderByDescending(m => GetComplexityMetricsLogic.Score(m, metric))
            .ThenBy(m => m.File, StringComparer.Ordinal)
            .ThenBy(m => m.Line)
            .ToList();

    /// <summary>
    /// max/avg/overThreshold describe the selected metric; maxCognitive is reported alongside so
    /// the other number stays visible even when filtering on cyclomatic.
    /// </summary>
    internal static object BuildSummary(
        IReadOnlyList<ComplexityMetric> items,
        int threshold,
        ComplexityMetricKind metric = ComplexityMetricKind.Cyclomatic)
    {
        if (items.Count == 0) return new { max = 0, avg = 0.0, overThreshold = 0, maxCognitive = 0 };
        var max = items.Max(m => GetComplexityMetricsLogic.Score(m, metric));
        var avg = items.Average(m => GetComplexityMetricsLogic.Score(m, metric));
        var overThreshold = items.Count(m => GetComplexityMetricsLogic.Score(m, metric) > threshold);
        var maxCognitive = items.Max(m => m.Cognitive);
        return new { max, avg, overThreshold, maxCognitive };
    }
}
