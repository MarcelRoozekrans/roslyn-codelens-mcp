using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Symbols;

/// <summary>
/// Compares <see cref="INamedTypeSymbol"/> by type signature: namespace, name, arity, and assembly name.
/// <para>
/// <b>Problem solved:</b> Roslyn compiles each project into its own <c>Compilation</c>, so a type like
/// <c>IFoo</c> defined in Project A appears as a different symbol object in Project A's compilation
/// (source symbol) and in Project B's compilation (metadata symbol). The default
/// <see cref="SymbolEqualityComparer"/> uses reference equality and treats these as different types,
/// causing cross-project implementors and derived types to go undetected in index lookups.
/// This comparer normalises across that boundary so any two symbols representing the same type definition
/// are considered equal regardless of which compilation produced them.
/// </para>
/// <para>
/// <b>Intentional omissions:</b> type parameter names are ignored because metadata symbols may use
/// different names than source symbols for the same parameter (e.g. <c>T</c> vs <c>TValue</c>).
/// Assembly version is ignored to tolerate multi-targeting and binding redirects.
/// </para>
/// </summary>
internal sealed class NamedTypeSignatureComparer : IEqualityComparer<INamedTypeSymbol>
{
	public static readonly NamedTypeSignatureComparer Instance = new();

	public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
	{
		if (ReferenceEquals(x, y)) return true;
		if (x is null || y is null) return false;
		return x.Arity == y.Arity
			&& string.Equals(x.Name, y.Name, StringComparison.Ordinal)
			&& string.Equals(x.ContainingNamespace.ToDisplayString(), y.ContainingNamespace.ToDisplayString(), StringComparison.Ordinal)
			&& string.Equals(x.ContainingAssembly?.Identity.Name, y.ContainingAssembly?.Identity.Name, StringComparison.Ordinal);
	}

	public int GetHashCode(INamedTypeSymbol obj)
	{
		return HashCode.Combine(
			obj.Name,
			obj.Arity,
			obj.ContainingNamespace.ToDisplayString(),
			obj.ContainingAssembly?.Identity.Name);
	}
}
