using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ListSolutionsTool
{
    private const int DefaultLimit = 50;

    [McpServerTool(Name = "list_solutions"),
     Description("List all solutions loaded by this server, showing which one is currently active. " +
                 "Each entry includes any projects that were skipped during load (e.g. legacy non-SDK-style projects), " +
                 "with the kind and reason for each skip. " +
                 "Returns an envelope with items sorted by solution path, totalCount, truncated, and limit (default 50).")]
    public static ToolListResult<SolutionInfo> Execute(
        MultiSolutionManager manager,
        [Description("Maximum number of items to return (default: 50). Items are sorted by solution path.")]
            int? limit = null)
    {
        var raw = manager.ListSolutions();
        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<SolutionInfo> Sort(IReadOnlyList<SolutionInfo> items)
        => items
            .OrderBy(s => s.Path, StringComparer.Ordinal)
            .ToList();
}
