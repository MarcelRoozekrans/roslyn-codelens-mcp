using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetProjectDependenciesTool
{
    [McpServerTool(Name = "get_project_dependencies"),
     Description("Return the project reference graph (direct and transitive dependencies)")]
    public static ProjectDependencyGraph? Execute(
        MultiSolutionManager manager,
        [Description("Project name or .csproj filename")] string project)
    {
        manager.EnsureLoaded();
        return GetProjectDependenciesLogic.Execute(manager.GetLoadedSolution(), project);
    }
}
