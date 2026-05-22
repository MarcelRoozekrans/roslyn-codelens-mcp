using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindReflectionUsageTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_reflection_usage"),
     Description("Detect dynamic/reflection-based usage like Type.GetType, Activator.CreateInstance, MethodInfo.Invoke. " +
                 "Returns an envelope with items, totalCount, truncated, limit (default 500), and a byKind summary.")]
    public static ToolListResult<ReflectionUsage> Execute(
        MultiSolutionManager manager,
        [Description("Optional type name to filter results (omit to scan entire solution)")] string? symbol = null,
        [Description("Maximum number of items to return (default: 500). Items are sorted by file, then line.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindReflectionUsageLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<ReflectionUsage> Sort(IReadOnlyList<ReflectionUsage> items)
        => items
            .OrderBy(r => r.File, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<ReflectionUsage> items)
    {
        var byKind = items
            .GroupBy(r => r.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byKind };
    }
}
