using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ListSolutionsTool
{
    [McpServerTool(Name = "list_solutions"),
     Description("List all solutions loaded by this server, showing which one is currently active. " +
                 "Each entry includes any projects that were skipped during load (e.g. legacy non-SDK-style projects), " +
                 "with the kind and reason for each skip.")]
    public static IReadOnlyList<SolutionInfo> Execute(MultiSolutionManager manager)
    {
        return manager.ListSolutions();
    }
}
