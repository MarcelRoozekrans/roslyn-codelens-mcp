using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindLargeClassesTool
{
    [McpServerTool(Name = "find_large_classes"),
     Description("Find classes and structs that exceed member count or line count thresholds")]
    public static IReadOnlyList<LargeClassInfo> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Maximum members before flagging (default: 20)")] int maxMembers = 20,
        [Description("Maximum lines before flagging (default: 500)")] int maxLines = 500)
    {
        manager.EnsureLoaded();
        return FindLargeClassesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project, maxMembers, maxLines);
    }
}
