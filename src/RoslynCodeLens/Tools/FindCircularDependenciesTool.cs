using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindCircularDependenciesTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "find_circular_dependencies"),
     Description("Detect circular dependencies in the project reference graph or namespace dependency graph. " +
                 "Returns an envelope with items sorted by cycle length desc, totalCount, truncated, and limit (default 100).")]
    public static ToolListResult<CircularDependency> Execute(
        MultiSolutionManager manager,
        [Description("Level: 'project' or 'namespace' (default: project)")] string level = "project",
        [Description("Maximum number of items to return (default: 100). Items are sorted by cycle length desc.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindCircularDependenciesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), level);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<CircularDependency> Sort(IReadOnlyList<CircularDependency> items)
        => items
            .OrderByDescending(c => c.Cycle.Count)
            .ToList();
}
