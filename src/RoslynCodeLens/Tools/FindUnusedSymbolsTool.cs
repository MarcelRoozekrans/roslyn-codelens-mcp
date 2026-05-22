using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindUnusedSymbolsTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_unused_symbols"),
     Description("Find potentially unused types and members (dead code detection). Checks public symbols for references across the solution. " +
                 "Returns an envelope with items, totalCount, truncated, limit (default 500), and a byKind summary.")]
    public static ToolListResult<UnusedSymbolInfo> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Include internal symbols (default: false)")] bool includeInternal = false,
        [Description("Maximum number of items to return (default: 500). Items are sorted by project, then file.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindUnusedSymbolsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project, includeInternal);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<UnusedSymbolInfo> Sort(IReadOnlyList<UnusedSymbolInfo> items)
        => items
            .OrderBy(u => u.Project, StringComparer.Ordinal)
            .ThenBy(u => u.File, StringComparer.Ordinal)
            .ThenBy(u => u.Line)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<UnusedSymbolInfo> items)
    {
        var byKind = items
            .GroupBy(u => u.SymbolKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byKind };
    }
}
