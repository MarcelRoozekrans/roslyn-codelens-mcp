using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindCircularDependenciesTool
{
    [McpServerTool(Name = "find_circular_dependencies"),
     Description("Detect circular dependencies in the project reference graph or namespace dependency graph")]
    public static IReadOnlyList<CircularDependency> Execute(
        MultiSolutionManager manager,
        [Description("Level: 'project' or 'namespace' (default: project)")] string level = "project")
    {
        manager.EnsureLoaded();
        return FindCircularDependenciesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), level);
    }
}
