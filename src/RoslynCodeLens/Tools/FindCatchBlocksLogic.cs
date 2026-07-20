using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Solution-wide scan for the catch clauses that handle a given exception type.
/// </summary>
public static class FindCatchBlocksLogic
{
    /// <summary>
    /// Every catch clause handling <paramref name="exceptionType"/>. With
    /// <paramref name="includeBaseClauses"/> a clause catching a base type — up to
    /// <c>catch (Exception)</c> and a bare <c>catch</c> — counts as a handler too, which is what
    /// makes "who actually swallows this?" answerable in one call.
    /// </summary>
    public static IReadOnlyList<CatchBlockInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string exceptionType,
        bool includeBaseClauses,
        CancellationToken cancellationToken = default)
    {
        var target = ExceptionQueries.ResolveExceptionType(resolver, metadata, exceptionType);

        var results = new List<CatchBlockInfo>();
        // A linked file belongs to several compilations; the same clause would repeat otherwise.
        var seen = new HashSet<(string File, int Line, int Column)>();

        // Which trees to walk — once each, generated ones skipped, project attribution
        // deterministic — is SolutionScanner's job; this loop only asks what each clause is.
        foreach (var scan in SolutionScanner.EnumerateTrees(loaded, resolver, cancellationToken: cancellationToken))
        {
            var model = scan.SemanticModel();

            var walkedNodes = 0;
            foreach (var clause in scan.Root.DescendantNodes().OfType<CatchClauseSyntax>())
            {
                // One machine-written file can hold hundreds of thousands of nodes, so a token
                // checked only per tree leaves the caller unable to cancel partway through one.
                if ((++walkedNodes & 0x3FF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var caught = DeclaredType(clause, model);
                if (!Matches(clause, caught, target, model, includeBaseClauses))
                    continue;

                var position = clause.GetLocation().GetLineSpan();
                var file = position.Path;
                var line = position.StartLinePosition.Line + 1;
                var column = position.StartLinePosition.Character + 1;

                if (!seen.Add((file, line, column)))
                    continue;

                results.Add(new CatchBlockInfo(
                    CaughtType: caught is null ? null : ExceptionQueries.Fqn(caught),
                    Method: ExceptionQueries.DescribeContainingMember(clause, model),
                    File: file,
                    Line: line,
                    Column: column,
                    HasFilter: clause.Filter != null,
                    Rethrows: Rethrows(clause),
                    IsEmpty: clause.Block.Statements.Count == 0,
                    Snippet: ExceptionQueries.CatchSnippet(clause),
                    Project: scan.ProjectName));
            }
        }

        return results;
    }

    /// <summary>The clause's declared exception type, or null for a bare <c>catch</c>.</summary>
    private static INamedTypeSymbol? DeclaredType(CatchClauseSyntax clause, SemanticModel model)
        => clause.Declaration?.Type is { } typeSyntax
            ? model.GetSymbolInfo(typeSyntax).Symbol as INamedTypeSymbol
            : null;

    private static bool Matches(
        CatchClauseSyntax clause,
        INamedTypeSymbol? caught,
        INamedTypeSymbol target,
        SemanticModel model,
        bool includeBaseClauses)
    {
        // Base/bare matching is exactly the analyzer's notion of "would this clause bind the
        // exception", so it is reused rather than re-derived here.
        if (includeBaseClauses)
            return ExceptionAnalyzer.CatchesType(clause, target, model);

        return caught != null
            && string.Equals(
                ExceptionQueries.Fqn(caught), ExceptionQueries.Fqn(target), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the handler rethrows. Nested lambdas and local functions are skipped: a
    /// <c>throw;</c> declared inside one runs when that body runs, not as part of this handler.
    /// </summary>
    private static bool Rethrows(CatchClauseSyntax clause)
        => clause.Block
            .DescendantNodes(descendIntoChildren: ExceptionAnalyzer.DescendIntoBody)
            .Any(n => n is ThrowStatementSyntax { Expression: null });
}
