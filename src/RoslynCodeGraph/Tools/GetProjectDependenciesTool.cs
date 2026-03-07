using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetProjectDependenciesLogic
{
    public static ProjectDependencyGraph? Execute(LoadedSolution loaded, string project)
    {
        var target = loaded.Solution.Projects
            .FirstOrDefault(p =>
                p.Name.Equals(project, StringComparison.OrdinalIgnoreCase) ||
                (p.FilePath != null && p.FilePath.Contains(project, StringComparison.OrdinalIgnoreCase)));

        if (target == null)
            return null;

        var direct = new List<ProjectRef>();
        foreach (var projRef in target.ProjectReferences)
        {
            var refProject = loaded.Solution.GetProject(projRef.ProjectId);
            if (refProject != null)
            {
                direct.Add(new ProjectRef(refProject.Name, refProject.FilePath ?? ""));
            }
        }

        var transitive = new List<ProjectRef>();
        var visited = new HashSet<string>(direct.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ProjectRef>(direct);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentProject = loaded.Solution.Projects
                .FirstOrDefault(p => p.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase));

            if (currentProject == null)
                continue;

            foreach (var projRef in currentProject.ProjectReferences)
            {
                var refProject = loaded.Solution.GetProject(projRef.ProjectId);
                if (refProject != null && !visited.Contains(refProject.Name))
                {
                    visited.Add(refProject.Name);
                    var pr = new ProjectRef(refProject.Name, refProject.FilePath ?? "");
                    transitive.Add(pr);
                    queue.Enqueue(pr);
                }
            }
        }

        return new ProjectDependencyGraph(direct, transitive);
    }
}

[McpServerToolType]
public static class GetProjectDependenciesTool
{
    [McpServerTool(Name = "get_project_dependencies"),
     Description("Return the project reference graph (direct and transitive dependencies)")]
    public static ProjectDependencyGraph? Execute(
        LoadedSolution loaded,
        [Description("Project name or .csproj filename")] string project)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return GetProjectDependenciesLogic.Execute(loaded, project);
    }
}
