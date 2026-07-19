using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Symbols;

/// <summary>
/// Shared grouping of resolver matches into logical targets: all overloads of one
/// method form a single group (keyed by containing type + name); every other symbol
/// groups by its full display string. Used by rename_symbol (one rename target) and
/// get_method_source (one logical request) so their ambiguity semantics stay identical.
/// </summary>
internal static class LogicalMemberGroups
{
    public static List<IGrouping<object, ISymbol>> GroupLogicalTargets(IReadOnlyList<ISymbol> symbols)
        => symbols.GroupBy(GroupKey).ToList();

    private static object GroupKey(ISymbol s) => s is IMethodSymbol m
        ? (m.ContainingType?.ToDisplayString() ?? "", m.Name)
        : s.ToDisplayString();
}
