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
/// <see cref="CreateFromDisk"/> loads document text from the files themselves via
/// SourceText.From(stream) — capturing the on-disk encoding/BOM exactly the way
/// MSBuildWorkspace's file loader does — for tests that exercise encoding
/// preservation on the write path.
/// </summary>
internal static class RenameTestWorkspace
{
    public static (LoadedSolution Loaded, SymbolResolver Resolver) Create(
        params (string FilePath, string Source)[] files)
        => Create(("RenameProj", files));

    public static (LoadedSolution Loaded, SymbolResolver Resolver) Create(
        params (string ProjectName, (string FilePath, string Source)[] Files)[] projects)
        => CreateCore(projects
            .Select(p => (p.ProjectName, p.Files
                .Select(f => (f.FilePath, SourceText.From(f.Source)))
                .ToArray()))
            .ToArray());

    /// <summary>
    /// Projects in a STRICT chain: each references only its immediate predecessor, so the last
    /// project depends on the first transitively but not directly. The multi-project overload
    /// above cannot express this — it references every earlier project — which makes it unable to
    /// distinguish direct from transitive dependents.
    /// </summary>
    public static (LoadedSolution Loaded, SymbolResolver Resolver) CreateChain(
        params (string ProjectName, (string FilePath, string Source)[] Files)[] projects)
        => CreateCore(
            projects
                .Select(p => (p.ProjectName, p.Files
                    .Select(f => (f.FilePath, SourceText.From(f.Source)))
                    .ToArray()))
                .ToArray(),
            chainOnly: true);

    /// <summary>
    /// Single project whose documents are read from existing files on disk, so each
    /// document's SourceText carries the detected encoding (incl. BOM presence).
    /// </summary>
    public static (LoadedSolution Loaded, SymbolResolver Resolver) CreateFromDisk(
        params string[] filePaths)
        => CreateCore(("RenameProj", filePaths
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return (path, SourceText.From(stream));
            })
            .ToArray()));

    private static (LoadedSolution Loaded, SymbolResolver Resolver) CreateCore(
        params (string ProjectName, (string FilePath, SourceText Text)[] Files)[] projects)
        => CreateCore(projects, chainOnly: false);

    private static (LoadedSolution Loaded, SymbolResolver Resolver) CreateCore(
        (string ProjectName, (string FilePath, SourceText Text)[] Files)[] projects, bool chainOnly)
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
                .WithProjectReferences(
                    (chainOnly ? projectIds.TakeLast(1) : projectIds)
                        .Select(id => new ProjectReference(id)));

            solution = solution.AddProject(projectInfo);
            foreach (var (path, text) in files)
            {
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId), Path.GetFileName(path),
                    text, filePath: path);
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
