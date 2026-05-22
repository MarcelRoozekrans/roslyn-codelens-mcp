using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class SearchSymbolsTool
{
    private const int DefaultLimit = 200;

    [McpServerTool(Name = "search_symbols"),
     Description("Search for types, methods, properties, and fields by name (case-insensitive substring match). " +
                 "Returns an envelope with items sorted by match quality (exact → prefix → substring), " +
                 "totalCount, truncated, limit (default 200), and a byKind summary.")]
    public static ToolListResult<SymbolLocation> Execute(
        MultiSolutionManager manager,
        [Description("Search query (substring match against symbol names)")] string query,
        [Description("Maximum number of items to return (default: 200). Items are sorted by match quality.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = SearchSymbolsLogic.Execute(manager.GetResolver(), manager.GetMetadataResolver(), query);

        var sorted = SortByMatchQuality(query, raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<SymbolLocation> SortByMatchQuality(string query, IReadOnlyList<SymbolLocation> items)
        => items
            .OrderBy(s => MatchRank(SimpleName(s.FullName), query))
            .ThenBy(s => s.FullName, StringComparer.Ordinal)
            .ToList();

    private static string SimpleName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot < 0 ? fullName : fullName[(lastDot + 1)..];
    }

    private static int MatchRank(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    internal static object BuildSummary(IReadOnlyList<SymbolLocation> items)
    {
        var byKind = items
            .GroupBy(s => s.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byKind };
    }
}
