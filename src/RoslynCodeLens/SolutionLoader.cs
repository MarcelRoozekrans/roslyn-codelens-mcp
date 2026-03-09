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
}
