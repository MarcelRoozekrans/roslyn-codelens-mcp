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
                 "Filters out test methods, MCP tools, source-generator output, MEF-composed services, and interop-laid-out fields. " +
                 "Returns an envelope with items, totalCount, truncated, limit (default 500), and a summary including byKind + filteredOut counts.")]
    public static ToolListResult<UnusedSymbolInfo> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Include internal symbols (default: false)")] bool includeInternal = false,
        [Description("Maximum number of items to return (default: 500). Items are sorted by project, then file.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var (raw, filteredCounts) = FindUnusedSymbolsLogic.Execute(
            manager.GetLoadedSolution(), manager.GetResolver(), project, includeInternal);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw, filteredCounts);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<UnusedSymbolInfo> Sort(IReadOnlyList<UnusedSymbolInfo> items)
        => items
            .OrderBy(u => u.Project, StringComparer.Ordinal)
            .ThenBy(u => u.File, StringComparer.Ordinal)
            .ThenBy(u => u.Line)
            .ToList();

    internal static object BuildSummary(
        IReadOnlyList<UnusedSymbolInfo> items,
        IReadOnlyDictionary<string, int> filteredCounts)
    {
        var byKind = items
            .GroupBy(u => u.SymbolKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var filteredOut = new
        {
            testMethod = filteredCounts.GetValueOrDefault("testMethod", 0),
            testContainer = filteredCounts.GetValueOrDefault("testContainer", 0),
            mcpTool = filteredCounts.GetValueOrDefault("mcpTool", 0),
            generated = filteredCounts.GetValueOrDefault("generated", 0),
            composition = filteredCounts.GetValueOrDefault("composition", 0),
            interop = filteredCounts.GetValueOrDefault("interop", 0),
        };

        return new { byKind, filteredOut };
    }
}
