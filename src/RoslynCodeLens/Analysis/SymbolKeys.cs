using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// The one spelling of "the same symbol" that survives crossing a compilation boundary.
/// </summary>
/// <remarks>
/// A symbol is scoped to the compilation that produced it, so the same declaration seen through two
/// compilations gives two symbols that are NOT <see cref="SymbolEqualityComparer"/>-equal. Any tool
/// that obtains a target from one compilation (the solution-wide indexes in
/// <see cref="SymbolResolver"/> keep whichever compilation reached a type first — an arbitrary,
/// run-varying choice) and then binds usages under another must compare by name or it will find
/// nothing, intermittently.
/// <para>
/// The exception-flow tools hit this first and standardised on fully-qualified display names; see
/// <see cref="ExceptionQueries.Fqn"/>, which now delegates here so the two cannot drift apart.
/// The format adds the containing type and parameter types on top of
/// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> — without them two overloads of one method
/// share a key, and a match against the <c>[Obsolete]</c> one would fire on the other. Member
/// options do not affect how a type renders, so a type's key is exactly the plain
/// fully-qualified name.
/// </para>
/// </remarks>
public static class SymbolKeys
{
    private static readonly SymbolDisplayFormat FqnFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeExplicitInterface)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType);

    /// <summary>
    /// A symbol's fully-qualified name — including generic arguments, and for members the containing
    /// type and parameter types — without the <c>global::</c> prefix.
    /// </summary>
    public static string Fqn(ISymbol symbol)
        => symbol.ToDisplayString(FqnFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);
}
