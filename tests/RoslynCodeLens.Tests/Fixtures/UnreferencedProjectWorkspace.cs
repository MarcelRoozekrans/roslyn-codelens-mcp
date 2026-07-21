using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Two projects sharing ONE linked source file, where the project the scanner visits first has no
/// metadata references at all — so its compilation cannot resolve <c>System.Threading.Tasks.Task</c>
/// or <c>System.IDisposable</c>.
/// <para>
/// That is not a contrived shape: MSBuildWorkspace silently drops a project's references when a
/// design-time build only half-succeeds, which this repo has hit before. The healthy project still
/// contains the same file, so a scan must still report what is in it.
/// </para>
/// <para>
/// <see cref="RenameTestWorkspace"/> cannot express this — it gives every project the corelib
/// reference.
/// </para>
/// </summary>
internal static class UnreferencedProjectWorkspace
{
    /// <summary>
    /// The reference-less project is named so it sorts FIRST under the scanner's ordinal
    /// project-name ordering, which is what makes it the one that claims the shared tree's
    /// first-one-wins dedupe slot.
    /// </summary>
    public static (LoadedSolution Loaded, SymbolResolver Resolver) Create(
        string fileName, string source)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var projectIds = new List<ProjectId>();

        foreach (var (name, referenced) in new[] { ("AaaBroken", false), ("ZzzHealthy", true) })
        {
            var projectId = ProjectId.CreateNewId();
            var info = ProjectInfo.Create(
                projectId, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            if (referenced)
            {
                info = info.WithMetadataReferences(
                    [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
            }

            solution = solution
                .AddProject(info)
                .AddDocument(
                    DocumentId.CreateNewId(projectId), Path.GetFileName(fileName),
                    SourceText.From(source), filePath: fileName);

            projectIds.Add(projectId);
        }

        var compilations = new ConcurrentDictionary<ProjectId, Compilation>();
        foreach (var projectId in projectIds)
        {
            compilations[projectId] = solution.GetProject(projectId)!
                .GetCompilationAsync().GetAwaiter().GetResult()!;
        }

        var loaded = new LoadedSolution { Solution = solution, Compilations = compilations };
        return (loaded, new SymbolResolver(loaded));
    }
}
