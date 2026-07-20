using Microsoft.CodeAnalysis;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Solution-wide scan for every place a given exception type is thrown.
/// </summary>
public static class FindThrowSitesLogic
{
    /// <summary>
    /// Every throw of <paramref name="exceptionType"/> in source.
    /// </summary>
    /// <remarks>
    /// Unlike <c>get_exception_flow</c> — which asks "what escapes THIS method?" and so stops at
    /// lambda / local-function boundaries — this scan walks every node in every tree.
    /// A throw inside a lambda is still a throw site in the file; it is simply attributed to the
    /// member that lexically contains it, because that is where a reader goes to look at it.
    /// </remarks>
    public static IReadOnlyList<ThrowSiteInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string exceptionType,
        bool includeDerived,
        CancellationToken cancellationToken = default)
    {
        var target = ExceptionQueries.ResolveExceptionType(resolver, metadata, exceptionType);

        var results = new List<ThrowSiteInfo>();
        // A linked file belongs to several projects and therefore several compilations; the same
        // physical throw would otherwise be reported once per compilation.
        var seen = new HashSet<(string File, int Line, int Column)>();
        // A multi-targeted project compiles the same file once per target framework. Skipping
        // repeats here — before the walk and the binding, not after — means the work is done once
        // instead of N times, and it also makes `Project` attribution deterministic: the first
        // compilation to reach a file is the one that gets to name it, consistently.
        var walkedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var projectName = resolver.GetProjectName(projectId);

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (GeneratedCodeDetector.IsGenerated(tree))
                    continue;

                if (!string.IsNullOrEmpty(tree.FilePath) && !walkedPaths.Add(tree.FilePath))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();

                var model = compilation.GetSemanticModel(tree);

                foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
                {
                    if (ExceptionAnalyzer.TryGetThrowSite(node, model) is not { } site)
                        continue;

                    var matches = includeDerived
                        ? ExceptionQueries.IsOrDerivesFrom(site.ExceptionType, target)
                        : string.Equals(
                            ExceptionQueries.Fqn(site.ExceptionType),
                            ExceptionQueries.Fqn(target),
                            StringComparison.Ordinal);

                    if (!matches)
                        continue;

                    var position = site.Node.GetLocation().GetLineSpan();
                    var file = position.Path;
                    var line = position.StartLinePosition.Line + 1;
                    var column = position.StartLinePosition.Character + 1;

                    if (!seen.Add((file, line, column)))
                        continue;

                    results.Add(new ThrowSiteInfo(
                        ExceptionType: ExceptionQueries.Fqn(site.ExceptionType),
                        Method: ExceptionQueries.DescribeContainingMember(site.Node, model),
                        File: file,
                        Line: line,
                        Column: column,
                        Snippet: ExceptionQueries.StatementSnippet(site.Node),
                        IsRethrow: site.IsRethrow,
                        Project: projectName));
                }
            }
        }

        return results;
    }
}
