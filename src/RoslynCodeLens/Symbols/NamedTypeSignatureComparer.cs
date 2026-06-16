using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Symbols;

// Compares INamedTypeSymbol by signature (namespace, name, arity, assembly name) rather than
// object identity. Roslyn creates a separate symbol object for each Compilation, so the same
// type appears as a different object in the declaring project versus any referencing project.
// SymbolEqualityComparer.Default treats those as different, breaking cross-project index lookups.
// Intentional omissions: type-parameter names (may differ between source and metadata) and
// assembly version (to tolerate multi-targeting and binding redirects).
// Null-safe on ContainingNamespace and ContainingAssembly to tolerate ErrorTypeSymbol
// instances (e.g. NoPiaMissingCanonicalTypeSymbol) that surface from AllInterfaces / BaseType
// when references are unresolved.
internal sealed class NamedTypeSignatureComparer : IEqualityComparer<INamedTypeSymbol>
{
	public static readonly NamedTypeSignatureComparer Instance = new();

	public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
	{
		if (ReferenceEquals(x, y)) return true;
		if (x is null || y is null) return false;
		return x.Arity == y.Arity
			&& string.Equals(x.Name, y.Name, StringComparison.Ordinal)
			&& string.Equals(x.ContainingNamespace?.ToDisplayString(), y.ContainingNamespace?.ToDisplayString(), StringComparison.Ordinal)
			&& string.Equals(x.ContainingAssembly?.Identity.Name, y.ContainingAssembly?.Identity.Name, StringComparison.Ordinal);
	}

	public int GetHashCode(INamedTypeSymbol obj)
	{
		return HashCode.Combine(
			obj.Name,
			obj.Arity,
			obj.ContainingNamespace?.ToDisplayString(),
			obj.ContainingAssembly?.Identity.Name);
	}
}
