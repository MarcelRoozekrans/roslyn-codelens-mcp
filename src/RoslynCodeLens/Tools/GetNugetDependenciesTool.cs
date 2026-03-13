using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetNugetDependenciesTool
{
    [McpServerTool(Name = "get_nuget_dependencies"),
     Description("List NuGet package references for projects in the solution")]
    public static NugetDependencyGraph? Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter (omit to list all)")] string? project = null)
    {
        manager.EnsureLoaded();
        return GetNugetDependenciesLogic.Execute(manager.GetLoadedSolution(), project);
    }
}
