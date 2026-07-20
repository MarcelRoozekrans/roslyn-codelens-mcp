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

        if (!DerivesFromException(resolved))
        {
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"'{resolved.ToDisplayString()}' does not derive from System.Exception.",
                new { exceptionType, resolved = resolved.ToDisplayString() });
        }

        return resolved;
    }

    /// <summary>Whether <paramref name="type"/> is System.Exception or a subclass of it.</summary>
    public static bool DerivesFromException(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (string.Equals(Fqn(current), "System.Exception", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="type"/> is <paramref name="target"/> or derives from it. Compares
    /// fully-qualified names rather than symbol identity because the two symbols can come from
    /// different compilations, where <c>SymbolEqualityComparer</c> reports a false negative.
    /// </summary>
    public static bool IsOrDerivesFrom(INamedTypeSymbol type, INamedTypeSymbol target)
    {
        var targetName = Fqn(target);
        for (var current = type; current != null; current = current.BaseType)
        {
            if (string.Equals(Fqn(current), targetName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The same type as seen by <paramref name="compilation"/>. Symbols are compilation-scoped, so
    /// a type resolved once up front is not <c>SymbolEqualityComparer</c>-equal to the same type
    /// bound in another project's compilation — which would silently break catch matching in a
    /// multi-project solution. Falls back to the original symbol when the lookup fails (a type
    /// declared in a project this compilation does not reference cannot be thrown there anyway).
    /// </summary>
    public static INamedTypeSymbol Localize(INamedTypeSymbol type, Compilation compilation)
        => compilation.GetTypeByMetadataName(MetadataName(type)) ?? type;

    private static string MetadataName(INamedTypeSymbol type)
    {
        var name = type.MetadataName;
        for (var container = type.ContainingType; container != null; container = container.ContainingType)
            name = $"{container.MetadataName}+{name}";

        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? name : $"{ns.ToDisplayString()}.{name}";
    }

    public static string Fqn(INamedTypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Display name of the member that lexically contains <paramref name="node"/> — the method a
    /// throw or catch lives in. A throw inside a lambda or local function reports the member that
    /// declares it, since that is the location a reader navigates to.
    /// </summary>
    public static string DescribeContainingMember(SyntaxNode node, SemanticModel model)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current is not MemberDeclarationSyntax member)
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

    /// <summary>The statement containing a node, trimmed to a readable length.</summary>
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
        var header = clause.Block is { } block
            ? clause.ToString()[..(block.SpanStart - clause.SpanStart)]
            : clause.ToString();

        return Truncate(header.Trim());
    }

    private static string Truncate(string text)
        => text.Length > MaxSnippetLength ? text[..MaxSnippetLength] + "..." : text;
}
