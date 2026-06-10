using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Symbols;

// Compares ISymbol instances by declared signature rather than object identity.
// Roslyn compiles each project separately, so the same type/method/member appears as a different
// object in the declaring compilation versus any referencing compilation.
// SymbolEqualityComparer.Default treats those as different, causing cross-project lookups
// (references, callers, unused-symbol detection) to miss matches.
// For methods, parameter type display strings are included to distinguish overloads.
// Assembly version and type-parameter names are ignored — see NamedTypeSignatureComparer.
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

	private static string ContainingTypeKey(ISymbol symbol)
	{
		if (symbol.ContainingType is { } ct)
			return NamedTypeSignatureComparer.Instance.GetHashCode(ct).ToString();
		if (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns)
			return ns.ToDisplayString();
		return string.Empty;
	}

	private static bool MethodEquals(IMethodSymbol? x, IMethodSymbol? y)
	{
		if (x is null || y is null) return false;
		if (!string.Equals(x.MetadataName, y.MetadataName, StringComparison.Ordinal)) return false;
		if (!NamedTypeSignatureComparer.Instance.Equals(x.ContainingType, y.ContainingType)) return false;
		if (x.Parameters.Length != y.Parameters.Length) return false;

		for (int i = 0; i < x.Parameters.Length; i++)
		{
			var xParam = x.Parameters[i].Type.ToDisplayString();
			var yParam = y.Parameters[i].Type.ToDisplayString();
			if (!string.Equals(xParam, yParam, StringComparison.Ordinal))
				return false;
		}

		return true;
	}

	private static int MethodHashCode(IMethodSymbol m)
	{
		var hc = new HashCode();
		hc.Add(m.MetadataName, StringComparer.Ordinal);
		hc.Add(NamedTypeSignatureComparer.Instance.GetHashCode(m.ContainingType));
		hc.Add(m.Parameters.Length);
		foreach (var p in m.Parameters)
			hc.Add(p.Type.ToDisplayString(), StringComparer.Ordinal);
		return hc.ToHashCode();
	}

	private static bool MemberEquals(ISymbol x, ISymbol y)
	{
		if (!string.Equals(x.MetadataName, y.MetadataName, StringComparison.Ordinal)) return false;
		return string.Equals(ContainingTypeKey(x), ContainingTypeKey(y), StringComparison.Ordinal);
	}

	private static int MemberHashCode(ISymbol s)
	{
		return HashCode.Combine(s.MetadataName, ContainingTypeKey(s));
	}
}
