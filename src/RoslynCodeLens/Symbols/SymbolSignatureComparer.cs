using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Symbols;

/// <summary>
/// Compares <see cref="ISymbol"/> instances by their declared signature rather than object identity.
/// <para>
/// <b>Problem solved:</b> Roslyn compiles each project into its own <c>Compilation</c>, so the same
/// type, method, or member appears as a different <see cref="ISymbol"/> object in each compilation
/// (a source symbol in the declaring project and a metadata symbol in every referencing project).
/// <see cref="SymbolEqualityComparer.Default"/> uses reference equality and treats these as different
/// symbols, causing cross-project lookups (references, unused-symbol detection, caller finding) to miss
/// matches entirely.
/// This comparer normalises across that boundary by comparing the structural signature:
/// containing type (via <see cref="NamedTypeSignatureComparer"/>), member name, and — for methods —
/// parameter type names to distinguish overloads.
/// </para>
/// <para>
/// <b>Intentional omissions:</b> assembly version and type parameter names are ignored for the same
/// reasons as in <see cref="NamedTypeSignatureComparer"/>.
/// </para>
/// </summary>
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

	// ── Named types ──────────────────────────────────────────────────────────

	private static string ContainingTypeKey(ISymbol symbol)
	{
		if (symbol.ContainingType is { } ct)
			return NamedTypeSignatureComparer.Instance.GetHashCode(ct).ToString();
		if (symbol.ContainingNamespace is { IsGlobalNamespace: false } ns)
			return ns.ToDisplayString();
		return string.Empty;
	}

	// ── Methods ───────────────────────────────────────────────────────────────

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

	// ── Other members (property, field, event, …) ────────────────────────────

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
