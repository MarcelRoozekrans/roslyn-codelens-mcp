using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetTypeHierarchyTool
{
    [McpServerTool(Name = "get_type_hierarchy"),
     Description("Walk up (base classes, interfaces) and down (derived types) from a type")]
    public static TypeHierarchy Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple or fully qualified)")] string symbol,
        [Description(SolutionArgument.Description)] string? solution = null)
    {
        var sm = manager.Resolve(solution);
        sm.EnsureLoaded();
        var context = sm.GetAnalysisContext();
        return GetTypeHierarchyLogic.Execute(context.Resolver, context.Metadata, symbol);
    }
}
