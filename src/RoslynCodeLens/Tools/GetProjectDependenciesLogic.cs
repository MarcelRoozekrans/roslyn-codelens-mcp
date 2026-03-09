using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

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

        var projectsByName = new Dictionary<string, Microsoft.CodeAnalysis.Project>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in loaded.Solution.Projects)
            projectsByName[p.Name] = p;

        var transitive = new List<ProjectRef>();
        var visited = new HashSet<string>(direct.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ProjectRef>(direct);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            projectsByName.TryGetValue(current.Name, out var currentProject);

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
