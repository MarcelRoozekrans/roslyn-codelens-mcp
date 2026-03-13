using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetTypeHierarchyTool
{
    [McpServerTool(Name = "get_type_hierarchy"),
     Description("Walk up (base classes, interfaces) and down (derived types) from a type")]
    public static TypeHierarchy? Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        manager.EnsureLoaded();
        return GetTypeHierarchyLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
