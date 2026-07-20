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

        // Which trees to walk — once each, generated ones skipped, project attribution
        // deterministic — is SolutionScanner's job; this loop only asks what each node is.
        foreach (var scan in SolutionScanner.EnumerateTrees(loaded, resolver, cancellationToken: cancellationToken))
        {
            var model = scan.SemanticModel();

            var walkedNodes = 0;
            foreach (var node in scan.Root.DescendantNodes())
            {
                // One machine-written file can hold hundreds of thousands of nodes, so a token
                // checked only per tree leaves the caller unable to cancel partway through one.
                if ((++walkedNodes & 0x3FF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

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
                    Project: scan.ProjectName));
            }
        }

        return results;
    }
}
