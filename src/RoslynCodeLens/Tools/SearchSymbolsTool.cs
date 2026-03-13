using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class SearchSymbolsTool
{
    [McpServerTool(Name = "search_symbols"),
     Description("Search for types, methods, properties, and fields by name (case-insensitive substring match, max 50 results)")]
    public static IReadOnlyList<SymbolLocation> Execute(
        MultiSolutionManager manager,
        [Description("Search query (substring match against symbol names)")] string query)
    {
        manager.EnsureLoaded();
        return SearchSymbolsLogic.Execute(manager.GetResolver(), query);
    }
}
