using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetSourceGeneratorsTool
{
    [McpServerTool(Name = "get_source_generators"),
     Description("List source generators and their output per project")]
    public static IReadOnlyList<SourceGeneratorInfo> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null)
    {
        manager.EnsureLoaded();
        return GetSourceGeneratorsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project);
    }
}
