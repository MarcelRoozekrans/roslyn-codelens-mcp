using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Symbols;

// Compares INamedTypeSymbol by signature (namespace, containing types, name, arity, canonical assembly)
// rather than object identity. Roslyn creates a separate symbol object for each Compilation, so the same
// type appears as a different object in the declaring project versus any referencing project.
// SymbolEqualityComparer.Default treats those as different, breaking cross-project index lookups.
// The containing-type chain keeps nested types apart from top-level types and from nested types of
// the same name in other outer types.
// The assembly is compared through the solution's TypeForwardingMap: the instances of a forwarded framework
// type (System.IDisposable in mscorlib, netstandard and System.Runtime) are one type, while two projects that
// each declare the same full name stay two types. The map is built per loaded solution, so the comparer is
// owned by its SymbolResolver.
// Intentional omissions: type-parameter names (may differ between source and metadata) and
// assembly version (to tolerate multi-targeting and binding redirects).
// Null-safe on ContainingNamespace and ContainingAssembly to tolerate ErrorTypeSymbol
// instances (e.g. NoPiaMissingCanonicalTypeSymbol) that surface from AllInterfaces / BaseType
// when references are unresolved.
internal sealed class NamedTypeSignatureComparer : IEqualityComparer<INamedTypeSymbol>
{
	private readonly TypeForwardingMap _forwarding;

	public NamedTypeSignatureComparer(TypeForwardingMap forwarding)
	{
		_forwarding = forwarding;
	}

	public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
	{
		if (ReferenceEquals(x, y)) return true;
		if (x is null || y is null) return false;
		if (x.Arity != y.Arity
			|| !string.Equals(x.Name, y.Name, StringComparison.Ordinal)
			|| !NamespacesEqual(x.ContainingNamespace, y.ContainingNamespace))
		{
			return false;
		}

		// A nested type lives in the assembly of its outermost type, so only the top level compares assemblies.
		if (x.ContainingType is not null || y.ContainingType is not null)
			return Equals(x.ContainingType, y.ContainingType);
		return string.Equals(_forwarding.GetCanonicalAssemblyName(x), _forwarding.GetCanonicalAssemblyName(y), StringComparison.Ordinal);
	}

	public int GetHashCode(INamedTypeSymbol obj)
	{
		return HashCode.Combine(
			obj.Name,
			obj.Arity,
			NamespaceHashCode(obj.ContainingNamespace),
			obj.ContainingType is { } containingType
				? GetHashCode(containingType)
				: StringComparer.Ordinal.GetHashCode(_forwarding.GetCanonicalAssemblyName(obj) ?? string.Empty));
	}

	// Name by name rather than through ToDisplayString(): the index hashes every type of every compilation.
	private static bool NamespacesEqual(INamespaceSymbol? x, INamespaceSymbol? y)
	{
		while (x is not null && y is not null)
		{
			if (x.IsGlobalNamespace || y.IsGlobalNamespace)
				return x.IsGlobalNamespace && y.IsGlobalNamespace;
			if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal))
				return false;

			x = x.ContainingNamespace;
			y = y.ContainingNamespace;
		}
		return x is null && y is null;
	}

	private static int NamespaceHashCode(INamespaceSymbol? ns)
	{
		var hash = new HashCode();
		for (; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
			hash.Add(ns.Name, StringComparer.Ordinal);
		return hash.ToHashCode();
	}
}