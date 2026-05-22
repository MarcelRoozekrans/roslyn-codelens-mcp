using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindCallersTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_callers"),
     Description("Find every call site for a method. " +
                 "Returns an envelope with items, totalCount, truncated, limit, and a byProject summary.")]
    public static ToolListResult<CallerInfo> Execute(
        MultiSolutionManager manager,
        [Description("Method name as Type.Method (simple or fully qualified)")] string symbol,
        [Description("Maximum number of items to return (default: 500). Items are sorted by file, then line.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindCallersLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), manager.GetMetadataResolver(), symbol);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<CallerInfo> Sort(IReadOnlyList<CallerInfo> items)
        => items
            .OrderBy(c => c.File, StringComparer.Ordinal)
            .ThenBy(c => c.Line)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<CallerInfo> items)
    {
        var byProject = items
            .GroupBy(c => c.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byProject };
    }
}
