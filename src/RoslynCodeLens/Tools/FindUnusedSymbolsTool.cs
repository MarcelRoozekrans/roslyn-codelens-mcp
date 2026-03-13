using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindUnusedSymbolsTool
{
    [McpServerTool(Name = "find_unused_symbols"),
     Description("Find potentially unused types and members (dead code detection). Checks public symbols for references across the solution.")]
    public static IReadOnlyList<UnusedSymbolInfo> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Include internal symbols (default: false)")] bool includeInternal = false)
    {
        manager.EnsureLoaded();
        return FindUnusedSymbolsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project, includeInternal);
    }
}
