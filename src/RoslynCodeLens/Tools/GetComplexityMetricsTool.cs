using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetComplexityMetricsTool
{
    [McpServerTool(Name = "get_complexity_metrics"),
     Description("Calculate cyclomatic complexity for methods. Returns methods exceeding the threshold, sorted by complexity.")]
    public static IReadOnlyList<ComplexityMetric> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum complexity threshold (default: 10)")] int threshold = 10)
    {
        manager.EnsureLoaded();
        return GetComplexityMetricsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project, threshold);
    }
}
