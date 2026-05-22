using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindLargeClassesTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "find_large_classes"),
     Description("Find classes and structs that exceed member count or line count thresholds. " +
                 "Returns an envelope with items sorted worst-first (highest size first), totalCount, truncated, and limit (default 100).")]
    public static ToolListResult<LargeClassInfo> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Maximum members before flagging (default: 20)")] int maxMembers = 20,
        [Description("Maximum lines before flagging (default: 500)")] int maxLines = 500,
        [Description("Maximum number of items to return (default: 100). Items are sorted by size desc (worst first).")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindLargeClassesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project, maxMembers, maxLines);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<LargeClassInfo> Sort(IReadOnlyList<LargeClassInfo> items)
        => items
            .OrderByDescending(c => Math.Max(c.MemberCount, c.LineCount))
            .ThenBy(c => c.TypeName, StringComparer.Ordinal)
            .ToList();
}
