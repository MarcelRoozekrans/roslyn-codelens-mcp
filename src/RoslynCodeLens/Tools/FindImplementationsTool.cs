using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindImplementationsTool
{
    private const int DefaultLimit = 200;

    [McpServerTool(Name = "find_implementations"),
     Description("Find all classes/structs implementing an interface or extending a class. " +
                 "Returns an envelope with items, totalCount, truncated, and limit.")]
    public static ToolListResult<SymbolLocation> Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple or fully qualified)")] string symbol,
        [Description("Maximum number of items to return (default: 200). Items are sorted by file, then line.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = FindImplementationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), manager.GetMetadataResolver(), symbol);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<SymbolLocation> Sort(IReadOnlyList<SymbolLocation> items)
        => items
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ToList();
}
