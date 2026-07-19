using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Builds an in-memory LoadedSolution + SymbolResolver from source strings.
/// Pass absolute paths as file names when a test needs apply-mode disk writes.
/// The multi-project overload adds projects in order, each referencing all
/// earlier ones (ProjectReference), so later projects are downstream dependents.
/// </summary>
internal static class RenameTestWorkspace
{
    public static (LoadedSolution Loaded, SymbolResolver Resolver) Create(
        params (string FilePath, string Source)[] files)
        => Create(("RenameProj", files));

    public static (LoadedSolution Loaded, SymbolResolver Resolver) Create(
        params (string ProjectName, (string FilePath, string Source)[] Files)[] projects)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var projectIds = new List<ProjectId>();

        foreach (var (projectName, files) in projects)
        {
            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                    projectId, VersionStamp.Create(), projectName, projectName, LanguageNames.CSharp,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .WithMetadataReferences(
                    [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)])
                .WithProjectReferences(projectIds.Select(id => new ProjectReference(id)));

            solution = solution.AddProject(projectInfo);
            foreach (var (path, source) in files)
            {
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId), Path.GetFileName(path),
                    SourceText.From(source), filePath: path);
            }

            projectIds.Add(projectId);
        }

        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        foreach (var projectId in projectIds)
        {
            compilations[projectId] = solution.GetProject(projectId)!
                .GetCompilationAsync().GetAwaiter().GetResult()!;
        }

        var loaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations,
        };
        return (loaded, new SymbolResolver(loaded));
    }
}
