using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Builds an in-memory single-project LoadedSolution + SymbolResolver from source
/// strings. Pass absolute paths as file names when a test needs apply-mode disk writes.
/// </summary>
internal static class RenameTestWorkspace
{
    public static (LoadedSolution Loaded, SymbolResolver Resolver) Create(
        params (string FilePath, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Create(), "RenameProj", "RenameProj", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        foreach (var (path, source) in files)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), Path.GetFileName(path),
                SourceText.From(source), filePath: path);
        }

        var compilation = solution.GetProject(projectId)!
            .GetCompilationAsync().GetAwaiter().GetResult()!;

        var loaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(
                [new KeyValuePair<ProjectId, Compilation>(projectId, compilation)]),
        };
        return (loaded, new SymbolResolver(loaded));
    }
}
