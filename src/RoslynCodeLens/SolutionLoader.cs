using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeLens;

public class SolutionLoader
{
    public async Task<LoadedSolution> LoadAsync(string solutionPath)
    {
        var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (_, e) =>
        {
            Console.Error.WriteLine($"[roslyn-codelens] Warning: {e.Diagnostic.Message}");
        };

        await Console.Error.WriteLineAsync($"[roslyn-codelens] Loading solution: {Path.GetFileName(solutionPath)}").ConfigureAwait(false);
        var solution = await workspace.OpenSolutionAsync(solutionPath).ConfigureAwait(false);

        var compilations = new Dictionary<ProjectId, Compilation>();
        var projects = solution.Projects.ToList();

        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Compiling project {i + 1}/{projects.Count}: {project.Name}").ConfigureAwait(false);

            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation != null)
            {
                compilations[project.Id] = compilation;
            }
        }

        await Console.Error.WriteLineAsync(
            $"[roslyn-codelens] Ready. {compilations.Count} projects compiled.").ConfigureAwait(false);

        return new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations
        };
    }

    public static string? FindSolutionFile(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            FileInfo? shortest = null;
            foreach (var f in dir.GetFiles("*.sln"))
            {
                if (shortest == null || f.FullName.Length < shortest.FullName.Length)
                    shortest = f;
            }
            foreach (var f in dir.GetFiles("*.slnx"))
            {
                if (shortest == null || f.FullName.Length < shortest.FullName.Length)
                    shortest = f;
            }
            if (shortest != null)
                return shortest.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Returns projects grouped by dependency level (leaves first).
    /// Level 0 = no project dependencies, Level 1 = depends only on level 0, etc.
    /// </summary>
    public static List<List<Project>> GetCompilationLevels(Solution solution)
    {
        var projects = solution.Projects.ToList();
        var projectIds = new HashSet<ProjectId>(projects.Select(p => p.Id));
        var assigned = new Dictionary<ProjectId, int>();
        var visiting = new HashSet<ProjectId>();

        int GetLevel(Project project)
        {
            if (assigned.TryGetValue(project.Id, out var cached))
                return cached;

            if (!visiting.Add(project.Id))
                return 0;

            var maxDep = -1;
            foreach (var dep in project.ProjectReferences)
            {
                if (!projectIds.Contains(dep.ProjectId))
                    continue;
                var depProject = solution.GetProject(dep.ProjectId);
                if (depProject != null)
                    maxDep = Math.Max(maxDep, GetLevel(depProject));
            }

            var level = maxDep + 1;
            assigned[project.Id] = level;
            visiting.Remove(project.Id);
            return level;
        }

        foreach (var project in projects)
            GetLevel(project);

        if (assigned.Count == 0)
            return [];

        var maxLevel = assigned.Values.Max();
        var levels = new List<List<Project>>(maxLevel + 1);
        for (var i = 0; i <= maxLevel; i++)
            levels.Add(new List<Project>());

        foreach (var project in projects)
            levels[assigned[project.Id]].Add(project);

        return levels;
    }
}
