using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Symbols;

// Compares ISymbol instances by declared signature rather than object identity.
// Roslyn compiles each project separately, so the same type/method/member appears as a different
// object in the declaring compilation versus any referencing compilation.
// SymbolEqualityComparer.Default treats those as different, causing cross-project lookups
// (references, callers, unused-symbol detection) to miss matches.
// Overloads are told apart by their full signature: parameter types and ref kinds for methods and
// indexers, plus the generic arity of methods and the return type of conversion operators, which C#
// may overload on.
// Assembly version and type-parameter names are ignored — see NamedTypeSignatureComparer.
// Parameter types and the conversion return type are compared by display string, which leaves out the
// assembly on purpose: a framework type lives in a different assembly per target framework (System.String
// is in mscorlib for net48 and in System.Runtime for net8.0, reached through type forwarding), so comparing
// assemblies would split a method of a net48 or netstandard project from its view in a net8.0 project.
// Roslyn's own Find References ignores assemblies for the same reason
// (SymbolEquivalenceComparer.IgnoreAssembliesInstance).
// Known limitation: two overloads in the same type whose parameter types share a full name but come from
// different assemblies are treated as one symbol. C# can declare them only through extern aliases, since
// referencing both types otherwise makes the name ambiguous (CS0433).
internal sealed class SymbolSignatureComparer : IEqualityComparer<ISymbol>
{
	public static readonly SymbolSignatureComparer Instance = new();

	public bool Equals(ISymbol? x, ISymbol? y)
	{
		if (ReferenceEquals(x, y)) return true;
		if (x is null || y is null) return false;
		if (x.Kind != y.Kind) return false;

		return x switch
		{
			INamedTypeSymbol nt => NamedTypeSignatureComparer.Instance.Equals(nt, y as INamedTypeSymbol),
			IMethodSymbol m => MethodEquals(m, y as IMethodSymbol),
			_ => MemberEquals(x, y)
		};
	}

	public int GetHashCode(ISymbol obj)
	{
		return obj switch
		{
			INamedTypeSymbol nt => NamedTypeSignatureComparer.Instance.GetHashCode(nt),
			IMethodSymbol m => MethodHashCode(m),
			_ => MemberHashCode(obj)
		};
	}

	private static bool MethodEquals(IMethodSymbol? x, IMethodSymbol? y)
	{
		if (x is null || y is null) return false;
		if (!string.Equals(x.MetadataName, y.MetadataName, StringComparison.Ordinal)) return false;
		if (x.Arity != y.Arity) return false;
		if (!NamedTypeSignatureComparer.Instance.Equals(x.ContainingType, y.ContainingType)) return false;
		if (!ParametersEqual(x.Parameters, y.Parameters)) return false;

		return x.MethodKind != MethodKind.Conversion
			|| string.Equals(x.ReturnType.ToDisplayString(), y.ReturnType.ToDisplayString(), StringComparison.Ordinal);
	}

	private static int MethodHashCode(IMethodSymbol m)
	{
		var hc = new HashCode();
		hc.Add(m.MetadataName, StringComparer.Ordinal);
		hc.Add(m.Arity);
		hc.Add(NamedTypeSignatureComparer.Instance.GetHashCode(m.ContainingType));
		AddParameters(ref hc, m.Parameters);
		if (m.MethodKind == MethodKind.Conversion)
			hc.Add(m.ReturnType.ToDisplayString(), StringComparer.Ordinal);
		return hc.ToHashCode();
	}

	// Indexers are the only overloadable members besides methods; for every other member the
	// parameter lists are empty.
	private static bool MemberEquals(ISymbol x, ISymbol y)
	{
		if (!string.Equals(x.MetadataName, y.MetadataName, StringComparison.Ordinal)) return false;
		if (!ContainerEquals(x, y)) return false;
		return ParametersEqual(IndexerParameters(x), IndexerParameters(y));
	}

	private static int MemberHashCode(ISymbol s)
	{
		var hc = new HashCode();
		hc.Add(s.MetadataName, StringComparer.Ordinal);
		hc.Add(ContainerHashCode(s));
		AddParameters(ref hc, IndexerParameters(s));
		return hc.ToHashCode();
	}

	private static bool ContainerEquals(ISymbol x, ISymbol y)
	{
		if (x.ContainingType is not null || y.ContainingType is not null)
			return NamedTypeSignatureComparer.Instance.Equals(x.ContainingType, y.ContainingType);
		return string.Equals(NamespaceKey(x), NamespaceKey(y), StringComparison.Ordinal);
	}

	private static int ContainerHashCode(ISymbol symbol)
	{
		return symbol.ContainingType is { } containingType
			? NamedTypeSignatureComparer.Instance.GetHashCode(containingType)
			: StringComparer.Ordinal.GetHashCode(NamespaceKey(symbol));
	}

	private static string NamespaceKey(ISymbol symbol)
	{
		return symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty;
	}

	private static ImmutableArray<IParameterSymbol> IndexerParameters(ISymbol symbol)
	{
		return symbol is IPropertySymbol property ? property.Parameters : [];
	}

	// Display strings leave out the assembly on purpose, see the header comment.
	private static bool ParametersEqual(ImmutableArray<IParameterSymbol> x, ImmutableArray<IParameterSymbol> y)
	{
		if (x.Length != y.Length) return false;

		for (int i = 0; i < x.Length; i++)
		{
			if (x[i].RefKind != y[i].RefKind)
				return false;
			if (!string.Equals(x[i].Type.ToDisplayString(), y[i].Type.ToDisplayString(), StringComparison.Ordinal))
				return false;
		}

		return true;
	}

	private static void AddParameters(ref HashCode hc, ImmutableArray<IParameterSymbol> parameters)
	{
		hc.Add(parameters.Length);
		foreach (var p in parameters)
		{
			hc.Add(p.RefKind);
			hc.Add(p.Type.ToDisplayString(), StringComparer.Ordinal);
		}
	}
}
