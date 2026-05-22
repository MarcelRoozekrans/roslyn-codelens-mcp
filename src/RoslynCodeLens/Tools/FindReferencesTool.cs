using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindReferencesTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_references"),
     Description("Find all references to a symbol (type, method, property, field, or event) across the solution. " +
                 "Returns an envelope with items, totalCount, truncated, limit, and a byProject summary.")]
    public static ToolListResult<SymbolReference> Execute(
        MultiSolutionManager manager,
        [Description("Symbol name: simple type (MyClass), fully qualified (Namespace.MyClass), or member (MyClass.MyProperty)")] string symbol,
        [Description("Maximum number of items to return (default: 500). Items are sorted by file, then line.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindReferencesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), manager.GetMetadataResolver(), symbol);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<SymbolReference> Sort(IReadOnlyList<SymbolReference> items)
        => items
            .OrderBy(r => r.File, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<SymbolReference> items)
    {
        var byProject = items
            .GroupBy(r => r.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byProject };
    }
}
