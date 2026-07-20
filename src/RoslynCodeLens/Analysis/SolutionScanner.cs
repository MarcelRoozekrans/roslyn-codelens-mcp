using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// One scannable syntax tree, with everything a solution-wide walk needs to interpret it.
/// </summary>
/// <param name="SemanticModel">
/// Deliberately a factory, not a model. Callers filter trees on purely syntactic grounds — declared
/// namespaces, say — and binding a tree that is about to be discarded is the single most expensive
/// thing such a scan can do. Creating the model on demand keeps that filtering worth having.
/// <para>
/// The factory is MEMOISED: the first call builds the model, every later call on the same
/// <see cref="ScanTree"/> hands back that same instance. A caller that reaches for the model in two
/// places in its loop body wants one model, not two — and building a second would quietly cost as
/// much as the filtering saves.
/// </para>
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
    /// An extra dimension on the dedupe key, computed from the project name and the tree alone —
    /// deliberately NOT from a <see cref="ScanTree"/>, because the key is decided before a
    /// <see cref="ScanTree"/> exists (see the dedupe-before-parse note below). "Walk once" means
    /// different things per caller: a linked file has one namespace across compilations but a
    /// DIFFERENT project per compilation, so a caller working in project terms must see it once per
    /// project or it silently loses every project after the first. Null — the common case — dedupes
    /// on tree identity alone.
    /// </param>
    /// <param name="modelFactory">
    /// Test observability seam ONLY; production callers leave it null and get
    /// <see cref="Compilation.GetSemanticModel"/>. It exists so a test can COUNT how many semantic
    /// models a scan creates — the lazy-model design's whole payoff is a count, and a count is not
    /// otherwise observable from outside.
    /// </param>
    /// <param name="rootFactory">
    /// Test observability seam ONLY; production callers leave it null and get
    /// <see cref="SyntaxTree.GetRoot"/>. It exists so a test can COUNT how many roots a scan
    /// realises, which is how "duplicates are never parsed" is pinned.
    /// </param>
    public static IEnumerable<ScanTree> EnumerateTrees(
        LoadedSolution loaded,
        SymbolResolver resolver,
        Func<string, bool>? projectFilter = null,
        Func<string, SyntaxTree, string>? scopeDiscriminator = null,
        Func<Compilation, SyntaxTree, SemanticModel>? modelFactory = null,
        Func<SyntaxTree, CancellationToken, SyntaxNode>? rootFactory = null,
        CancellationToken cancellationToken = default)
    {
        var walked = new HashSet<(string Scope, string Identity)>();
        modelFactory ??= static (compilation, tree) => compilation.GetSemanticModel(tree);
        rootFactory ??= static (tree, ct) => tree.GetRoot(ct);

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

                // Dedupe BEFORE parsing. Every input the key needs — project name and tree identity
                // — is available without a root, and realising a root is the expensive part: a
                // project multi-targeted across four frameworks presents the same file in four
                // compilations, and deciding the duplicate after building a ScanTree would parse it
                // four times to throw three away.
                var scope = scopeDiscriminator?.Invoke(projectName, tree) ?? string.Empty;
                if (!walked.Add((scope, TreeIdentity(tree, cancellationToken))))
                    continue;

                var compilationForModel = compilation;
                var treeForModel = tree;
                var model = new Lazy<SemanticModel>(
                    () => modelFactory(compilationForModel, treeForModel));

                yield return new ScanTree(
                    projectId,
                    projectName,
                    compilation,
                    tree,
                    rootFactory(tree, cancellationToken),
                    () => model.Value);
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
