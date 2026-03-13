using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindCallersTool
{
    [McpServerTool(Name = "find_callers"),
     Description("Find every call site for a method")]
    public static IReadOnlyList<CallerInfo> Execute(
        MultiSolutionManager manager,
        [Description("Method name as Type.Method (simple or fully qualified)")] string symbol)
    {
        manager.EnsureLoaded();
        return FindCallersLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
