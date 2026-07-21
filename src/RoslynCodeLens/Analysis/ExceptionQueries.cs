using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Plumbing shared by the three exception tools (<c>find_throw_sites</c>,
/// <c>find_catch_blocks</c>, <c>get_exception_flow</c>): resolving the requested exception type,
/// re-binding a type into another compilation, naming the member that contains a node, and
/// formatting snippets. Kept in one place so the three tools cannot drift apart.
/// </summary>
internal static class ExceptionQueries
{
    private const int MaxSnippetLength = 200;

    /// <summary>
    /// Resolve a user-supplied exception type name through the source index first and metadata
    /// second, so both <c>MyApp.DomainException</c> and <c>System.ArgumentNullException</c> work.
    /// </summary>
    /// <exception cref="McpToolException">
    /// <c>SymbolNotFound</c> when nothing resolves, <c>InvalidArgument</c> when what resolves is
    /// not an exception type.
    /// </exception>
    public static INamedTypeSymbol ResolveExceptionType(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string exceptionType)
    {
        var resolved = resolver.FindSymbols(exceptionType).OfType<INamedTypeSymbol>().FirstOrDefault()
            ?? metadata.Resolve(exceptionType)?.Symbol as INamedTypeSymbol;

        if (resolved is null or IErrorTypeSymbol)
        {
            throw new McpToolException(
                ToolErrorCode.SymbolNotFound,
                $"Exception type '{exceptionType}' not found.",
                new { exceptionType });
        }

        if (!IsOrDerivesFrom(resolved, "System.Exception"))
        {
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"'{resolved.ToDisplayString()}' does not derive from System.Exception.",
                new { exceptionType, resolved = resolved.ToDisplayString() });
        }

        return resolved;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is <paramref name="target"/> or derives from it.
    /// </summary>
    /// <remarks>
    /// The one and only exception-type comparison in this feature, so the three tools cannot
    /// disagree about what "same type" means. Fully-qualified display names, not symbol identity:
    /// symbols are compilation-scoped, so the same type bound in two projects is not
    /// <c>SymbolEqualityComparer</c>-equal; and generic arguments are part of the name, so
    /// <c>MyEx&lt;string&gt;</c> matches <c>MyEx&lt;string&gt;</c> and not <c>MyEx&lt;int&gt;</c>.
    /// </remarks>
    public static bool IsOrDerivesFrom(INamedTypeSymbol type, INamedTypeSymbol target)
        => IsOrDerivesFrom(type, Fqn(target));

    /// <summary>
    /// Overload for the one target that has no symbol to hand: <c>System.Exception</c> during
    /// argument validation, before any compilation has been chosen.
    /// </summary>
    public static bool IsOrDerivesFrom(INamedTypeSymbol type, string targetFqn)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (string.Equals(Fqn(current), targetFqn, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A type's fully-qualified name including generic arguments, without the <c>global::</c>
    /// prefix — the canonical spelling every comparison and every reported <c>exceptionType</c>
    /// goes through.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="SymbolKeys.Fqn"/>, which generalises this convention to members for
    /// the other tools that match across compilations. The extra member/parameter options that
    /// helper carries do not affect how a TYPE renders, so this spelling is unchanged.
    /// </remarks>
    public static string Fqn(INamedTypeSymbol type) => SymbolKeys.Fqn(type);

    /// <summary>
    /// Display name of the member that lexically contains <paramref name="node"/> — the method a
    /// throw or catch lives in. A throw inside a lambda or local function reports the member that
    /// declares it, since that is the location a reader navigates to.
    /// </summary>
    /// <remarks>
    /// A namespace declaration is a <see cref="MemberDeclarationSyntax"/> too, and it does declare
    /// a symbol, so without the skip below a node that sits in no type at all would be described as
    /// its namespace — naming a scope rather than a member, which is never what the caller wants.
    /// Exception sites are always inside a member so this is a no-op for them; <c>check_architecture</c>
    /// walks every name node and does hit the case.
    /// </remarks>
    public static string DescribeContainingMember(SyntaxNode node, SemanticModel model)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current is not MemberDeclarationSyntax member || member is BaseNamespaceDeclarationSyntax)
                continue;

            var symbol = model.GetDeclaredSymbol(member)
                ?? (member is BaseFieldDeclarationSyntax field
                    ? model.GetDeclaredSymbol(field.Declaration.Variables[0])
                    : null);

            if (symbol != null)
                return symbol.ToDisplayString();
        }

        return string.Empty;
    }

    /// <summary>
    /// The statement containing a node, trimmed to a readable length. Shared with
    /// <c>FindReferencesLogic</c>, which grew a byte-identical private copy — one helper so the
    /// snippet a user sees is formatted the same way whichever tool produced it.
    /// </summary>
    public static string StatementSnippet(SyntaxNode node)
    {
        var statement = node.FirstAncestorOrSelf<StatementSyntax>();
        return Truncate((statement ?? node.Parent ?? node).ToString());
    }

    /// <summary>
    /// A catch clause's header — <c>catch (T) when (...)</c> — without its body, which would
    /// otherwise swamp the snippet with the whole handler.
    /// </summary>
    public static string CatchSnippet(CatchClauseSyntax clause)
    {
        var headerLength = clause.Block.SpanStart - clause.SpanStart;
        var header = headerLength > 0 ? clause.ToString()[..headerLength] : clause.ToString();
        return Truncate(header.Trim());
    }

    private static string Truncate(string text)
        => text.Length > MaxSnippetLength ? text[..MaxSnippetLength] + "..." : text;
}
