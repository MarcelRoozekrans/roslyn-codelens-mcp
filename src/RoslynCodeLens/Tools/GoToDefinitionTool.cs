using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GoToDefinitionTool
{
    private const int DefaultLimit = 50;

    [McpServerTool(Name = "go_to_definition"),
     Description("Find the source file and line where a symbol is defined. " +
                 "Returns an envelope with items, totalCount, truncated, and limit.")]
    public static ToolListResult<SymbolLocation> Execute(
        MultiSolutionManager manager,
        [Description("Symbol name: simple type (MyClass), fully qualified (Namespace.MyClass), or member (MyClass.DoWork)")] string symbol,
        [Description("Maximum number of items to return (default: 50). Items are sorted by file, then line.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var raw = GoToDefinitionLogic.Execute(manager.GetResolver(), manager.GetMetadataResolver(), symbol);

        var sorted = Sort(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<SymbolLocation> Sort(IReadOnlyList<SymbolLocation> items)
        => items
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ThenBy(s => s.Line)
            .ToList();
}
