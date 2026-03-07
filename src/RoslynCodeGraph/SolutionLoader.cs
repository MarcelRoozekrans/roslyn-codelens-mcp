using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeGraph;

public class LoadedSolution
{
    public required Solution Solution { get; init; }
    public required Dictionary<ProjectId, Compilation> Compilations { get; init; }
    public bool IsEmpty => Compilations.Count == 0;

    public static LoadedSolution Empty { get; } = CreateEmpty();

    private static LoadedSolution CreateEmpty()
    {
        var workspace = new AdhocWorkspace();
        return new LoadedSolution
        {
            Solution = workspace.CurrentSolution,
            Compilations = new Dictionary<ProjectId, Compilation>()
        };
    }
}

public class SolutionLoader
{
    public async Task<LoadedSolution> LoadAsync(string solutionPath)
    {
        var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (_, e) =>
        {
            Console.Error.WriteLine($"[roslyn-codegraph] Warning: {e.Diagnostic.Message}");
        };

        Console.Error.WriteLine($"[roslyn-codegraph] Loading solution: {Path.GetFileName(solutionPath)}");
        var solution = await workspace.OpenSolutionAsync(solutionPath);

        var compilations = new Dictionary<ProjectId, Compilation>();
        var projects = solution.Projects.ToList();

        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];
            Console.Error.WriteLine(
                $"[roslyn-codegraph] Compiling project {i + 1}/{projects.Count}: {project.Name}");

            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
            {
                compilations[project.Id] = compilation;
            }
        }

        Console.Error.WriteLine(
            $"[roslyn-codegraph] Ready. {compilations.Count} projects compiled.");

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
            var slnFiles = dir.GetFiles("*.sln")
                .Concat(dir.GetFiles("*.slnx"))
                .ToArray();
            if (slnFiles.Length > 0)
            {
                return slnFiles
                    .OrderBy(f => f.FullName.Length)
                    .First()
                    .FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
