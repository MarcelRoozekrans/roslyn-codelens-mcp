using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// One scannable syntax tree, with everything a solution-wide walk needs to interpret it.
/// </summary>
/// <param name="SemanticModel">
/// Deliberately a factory, not a model. Callers filter trees on purely syntactic grounds — declared
/// namespaces, say — and binding a tree that is about to be discarded is the single most expensive
/// thing such a scan can do. Creating the model on demand keeps that filtering worth having.
/// </param>
public sealed record ScanTree(
    ProjectId ProjectId,
    string ProjectName,
    Compilation Compilation,
    SyntaxTree Tree,
    SyntaxNode Root,
    Func<SemanticModel> SemanticModel);

/// <summary>
/// The enumeration half — and only the enumeration half — of a solution-wide syntax scan:
/// which trees a tool should look at, exactly once each. What to ask of each node stays with the
/// caller, because the tools that use this ask genuinely different questions.
/// </summary>
public static class SolutionScanner
{
    /// <summary>
    /// Every non-generated tree in the solution, deduped.
    /// </summary>
    /// <param name="projectFilter">
    /// Receives a project name; returning false skips the whole compilation before any of its trees
    /// are touched. Null means every project.
    /// </param>
    /// <param name="scopeDiscriminator">
    /// An extra dimension on the dedupe key. "Walk once" means different things per caller: a linked
    /// file has one namespace across compilations but a DIFFERENT project per compilation, so a
    /// caller working in project terms must see it once per project or it silently loses every
    /// project after the first. Null — the common case — dedupes on tree identity alone.
    /// </param>
    public static IEnumerable<ScanTree> EnumerateTrees(
        LoadedSolution loaded,
        SymbolResolver resolver,
        Func<string, bool>? projectFilter = null,
        Func<ScanTree, string>? scopeDiscriminator = null,
        CancellationToken cancellationToken = default)
    {
        var walked = new HashSet<(string Scope, string Identity)>();

        // Compilations live in a ConcurrentDictionary, whose enumeration order is an implementation
        // detail. Since dedupe is first-one-wins, that order DECIDES which project a linked or
        // multi-targeted file is attributed to — so the same query could report different projects
        // on different runs. Ordering here makes attribution a property of the solution instead.
        foreach (var (projectId, compilation) in loaded.Compilations
                     .OrderBy(p => resolver.GetProjectName(p.Key), StringComparer.Ordinal)
                     .ThenBy(p => p.Key.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectName = resolver.GetProjectName(projectId);
            if (projectFilter is not null && !projectFilter(projectName))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (GeneratedCodeDetector.IsGenerated(tree))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();

                var scan = new ScanTree(
                    projectId,
                    projectName,
                    compilation,
                    tree,
                    tree.GetRoot(cancellationToken),
                    () => compilation.GetSemanticModel(tree));

                var scope = scopeDiscriminator?.Invoke(scan) ?? string.Empty;
                if (!walked.Add((scope, TreeIdentity(tree, cancellationToken))))
                    continue;

                yield return scan;
            }
        }
    }

    /// <summary>
    /// A stable per-tree key for the walk dedupe. File path when there is one; otherwise a hash of
    /// the tree's own text, because a pathless tree (in-memory documents, some generators) is a
    /// different object in every compilation and would otherwise be walked — and counted — once per
    /// compilation it appears in.
    /// </summary>
    internal static string TreeIdentity(SyntaxTree tree, CancellationToken cancellationToken)
        => string.IsNullOrEmpty(tree.FilePath)
            ? "\0content:" + Convert.ToBase64String(tree.GetText(cancellationToken).GetContentHash().AsSpan())
            : tree.FilePath;
}
