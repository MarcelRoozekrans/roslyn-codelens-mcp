using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindImplementationsTool
{
    [McpServerTool(Name = "find_implementations"),
     Description("Find all classes/structs implementing an interface or extending a class")]
    public static IReadOnlyList<SymbolLocation> Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        manager.EnsureLoaded();
        return FindImplementationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
