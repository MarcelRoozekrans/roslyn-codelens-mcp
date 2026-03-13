using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ListSolutionsTool
{
    [McpServerTool(Name = "list_solutions"),
     Description("List all solutions loaded by this server, showing which one is currently active.")]
    public static IReadOnlyList<SolutionInfo> Execute(MultiSolutionManager manager)
    {
        return manager.ListSolutions();
    }
}
