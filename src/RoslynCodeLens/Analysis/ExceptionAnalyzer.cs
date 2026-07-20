using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Analysis;

/// <summary>One explicit throw found in a method body (not in a nested lambda/local function).</summary>
public sealed record ThrowSite(INamedTypeSymbol ExceptionType, SyntaxNode Node, bool IsRethrow);

/// <summary>A catch clause that would handle an exception, plus whether it may decline.</summary>
public sealed record ExceptionHandler(CatchClauseSyntax Clause, bool HasFilter);

/// <summary>
/// Primitives for exception-flow analysis: which exceptions a body raises, which catch clauses
/// would handle them, and which exceptions a symbol documents. Pure: no I/O.
/// </summary>
public static class ExceptionAnalyzer
{
    /// <summary>
    /// Explicit throws in this body only. Lambdas, anonymous methods and local functions are
    /// separate execution bodies: a throw inside one escapes when IT runs — at whatever call
    /// site invokes the delegate, possibly in another method entirely — not at this method's
    /// boundary. Attributing them here is what made <c>generate_test_skeleton</c> emit
    /// assertions for exceptions the method under test cannot throw.
    /// </summary>
    public static IReadOnlyList<ThrowSite> CollectThrowSites(SyntaxNode body, SemanticModel model)
    {
        var sites = new List<ThrowSite>();

        // The predicate decides whether a node's CHILDREN are visited; the node itself is still
        // yielded. That is exactly what is wanted here — a nested lambda is seen but not entered.
        foreach (var node in body.DescendantNodes(descendIntoChildren: DescendIntoBody))
        {
            if (TryGetThrowSite(node, model) is { } site)
                sites.Add(site);
        }

        return sites;
    }

    /// <summary>
    /// The throw site this single node represents, or null when it is not a throw at all.
    /// Split out from <see cref="CollectThrowSites"/> so callers that need a different traversal
    /// — the solution-wide scan behind <c>find_throw_sites</c>, which deliberately DOES look
    /// inside lambdas — classify a throw exactly the same way.
    /// </summary>
    public static ThrowSite? TryGetThrowSite(SyntaxNode node, SemanticModel model)
        => node switch
        {
            ThrowStatementSyntax { Expression: null } rethrow
                => EnclosingCatchType(rethrow, model) is { } type
                    ? new ThrowSite(type, rethrow, IsRethrow: true)
                    : null,
            ThrowStatementSyntax { Expression: { } thrown } statement
                => Thrown(statement, thrown, model),
            ThrowExpressionSyntax expression
                => Thrown(expression, expression.Expression, model),
            _ => null,
        };

    /// <summary>
    /// The static type of a throw operand: <c>throw new T()</c>, <c>throw ex</c> and
    /// <c>throw Make()</c> all land here. A dynamically thrown runtime type is out of reach of
    /// static analysis.
    /// </summary>
    private static ThrowSite? Thrown(SyntaxNode node, ExpressionSyntax expression, SemanticModel model)
        => model.GetTypeInfo(expression).Type is INamedTypeSymbol type
            ? new ThrowSite(type, node, IsRethrow: false)
            : null;

    /// <summary>
    /// Whether a walk of one method body should enter this node's children. Shared so callers
    /// that need the same "this body only" boundary (rethrow detection inside a catch, for
    /// example) draw the line in exactly the same place.
    /// </summary>
    public static bool DescendIntoBody(SyntaxNode node)
        => node is not (LambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax
            or LocalFunctionStatementSyntax);

    /// <summary>
    /// The type a bare <c>throw;</c> rethrows: the enclosing clause's declared type, or
    /// <c>System.Exception</c> when the clause is a bare <c>catch</c>, which binds everything.
    /// </summary>
    private static INamedTypeSymbol? EnclosingCatchType(SyntaxNode node, SemanticModel model)
    {
        var clause = node.FirstAncestorOrSelf<CatchClauseSyntax>();
        if (clause == null)
            return null;

        if (clause.Declaration?.Type is { } typeSyntax
            && model.GetSymbolInfo(typeSyntax).Symbol is INamedTypeSymbol declared)
        {
            return declared;
        }

        return model.Compilation.GetTypeByMetadataName("System.Exception");
    }

    /// <summary>
    /// Whether this clause would bind <paramref name="exceptionType"/>: a bare <c>catch</c>
    /// binds everything, otherwise the declared type must be the type itself or a base of it.
    /// A <c>when</c> filter is deliberately ignored — it decides at runtime, so callers treat it
    /// separately rather than letting it turn a match into a non-match.
    /// </summary>
    /// <remarks>
    /// Matching goes through <see cref="ExceptionQueries.IsOrDerivesFrom"/>, which compares
    /// fully-qualified display names. <c>SymbolEqualityComparer</c> is wrong here twice over: the
    /// thrown type may have been bound in a different project's compilation, and a constructed
    /// generic (<c>MyEx&lt;string&gt;</c>) is never equal to the unbound definition a
    /// metadata-name lookup hands back. One comparison strategy, used everywhere.
    /// </remarks>
    public static bool CatchesType(
        CatchClauseSyntax clause, INamedTypeSymbol exceptionType, SemanticModel model)
    {
        if (clause.Declaration?.Type is not { } typeSyntax)
            return true;

        if (model.GetSymbolInfo(typeSyntax).Symbol is not INamedTypeSymbol caught)
            return false;

        return ExceptionQueries.IsOrDerivesFrom(exceptionType, caught);
    }

    /// <summary>
    /// Every catch clause that could handle this exception, innermost try first and, within one
    /// try, in source order — the order the CLR itself considers them.
    /// </summary>
    /// <remarks>
    /// Deliberately an enumeration rather than a "first handler": a clause carrying a
    /// <c>when</c> filter may decline at run time, so it does not end the search. Returning only
    /// the first binding clause meant <c>catch (IOException) when (c) { } catch (IOException) { }</c>
    /// reported the exception as escaping, even though the second clause always catches it.
    /// Callers walk this sequence and stop at the first UNFILTERED candidate.
    /// </remarks>
    public static IEnumerable<ExceptionHandler> EnumerateHandlers(
        SyntaxNode node, INamedTypeSymbol exceptionType, SemanticModel model)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (IsBodyBoundary(current))
                break;

            // A try protects only the statements in its own try block. Reaching the try through
            // any other child — a catch body, the filter, or the finally block — means the node
            // is not protected by this try, so its clauses must not be consulted. Testing
            // `tryStatement.Block == current` is what encodes that: only the try block itself
            // ever satisfies it, because the walk arrives at the try from the child it came
            // through (the catch clause, or the finally clause, for the unprotected positions).
            if (current.Parent is TryStatementSyntax tryStatement && tryStatement.Block == current)
            {
                foreach (var clause in tryStatement.Catches)
                {
                    if (CatchesType(clause, exceptionType, model))
                        yield return new ExceptionHandler(clause, clause.Filter != null);
                }
            }
        }
    }

    /// <summary>
    /// The outer edge of one execution body. Lambdas and local functions count: an exception
    /// raised inside one does not surface to the try/catch that lexically surrounds its
    /// declaration, only to whatever eventually invokes it.
    /// </summary>
    private static bool IsBodyBoundary(SyntaxNode node)
        => node is MemberDeclarationSyntax
            or LambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax
            or LocalFunctionStatementSyntax;

    /// <summary>
    /// <c>exception cref</c> entries from an XML documentation comment, in document order with
    /// the <c>T:</c> metadata prefix stripped.
    /// </summary>
    public static IReadOnlyList<string> ParseExceptionCrefs(string? documentationXml)
    {
        if (string.IsNullOrWhiteSpace(documentationXml))
            return [];

        var document = TryParse(documentationXml);
        if (document == null)
            return [];

        return document.Descendants("exception")
            .Select(e => e.Attribute("cref")?.Value)
            .Where(cref => !string.IsNullOrEmpty(cref))
            .Select(cref => cref!.StartsWith("T:", StringComparison.Ordinal) ? cref[2..] : cref!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// <c>GetDocumentationCommentXml</c> normally yields a single rooted <c>member</c> element,
    /// but a bare fragment (several sibling tags, no root) is not well-formed XML and would
    /// throw. Retry such input under a synthetic root before giving up.
    /// </summary>
    private static XDocument? TryParse(string xml)
    {
        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            try
            {
                return XDocument.Parse($"<root>{xml}</root>");
            }
            catch (System.Xml.XmlException)
            {
                return null;
            }
        }
    }

    /// <summary>Documented exception types of a symbol, resolved against a compilation.</summary>
    public static IReadOnlyList<INamedTypeSymbol> GetDocumentedExceptions(
        ISymbol symbol, Compilation compilation)
        => ParseExceptionCrefs(symbol.GetDocumentationCommentXml())
            .Select(compilation.GetTypeByMetadataName)
            .OfType<INamedTypeSymbol>()
            .ToList();
}
