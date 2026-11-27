using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Symbols;

// Assigns every type a canonical assembly name, so that the instances of one framework type compare equal across
// target frameworks. System.IDisposable lives in mscorlib for net48, in netstandard for netstandard2.0 and in
// System.Runtime for net8+; facade assemblies forward it between them. Each forwarder links
// (forwarding assembly, type) with (destination assembly, type); union-find over all links gives one set per
// logical type, and the ordinal minimum of its assembly names is the canonical one.
// The map spans the whole solution on purpose: a net48 compilation references only mscorlib, which forwards
// nothing, so only the facades seen by the other compilations link its types to netstandard and System.Runtime.
// Types that no forwarder mentions keep their own assembly name, so two projects that each declare the same full
// name stay distinct types.
internal sealed class TypeForwardingMap
{
	public static readonly TypeForwardingMap Empty = new(new Dictionary<TypeKey, string>());

	private readonly Dictionary<TypeKey, string> _canonicalAssemblies;
	// Assemblies that take part in forwarding; the types of all others keep their own assembly name.
	private readonly HashSet<string> _forwardingAssemblies;

	private TypeForwardingMap(Dictionary<TypeKey, string> canonicalAssemblies)
	{
		_canonicalAssemblies = canonicalAssemblies;
		_forwardingAssemblies = new HashSet<string>(canonicalAssemblies.Keys.Select(k => k.Assembly), StringComparer.Ordinal);
	}

	public static TypeForwardingMap Build(IEnumerable<Compilation> compilations)
	{
		var parents = new Dictionary<TypeKey, TypeKey>();
		// A reference assembly file is scanned once. Its identity is not enough: net48's mscorlib and the mscorlib
		// facade of net10.0 are both mscorlib 4.0.0.0, but only the facade forwards the framework types.
		var scannedFiles = new HashSet<string>(StringComparer.Ordinal);

		foreach (var compilation in compilations)
		{
			AddForwarders(parents, compilation.Assembly);
			foreach (var reference in compilation.References)
			{
				if (reference is PortableExecutableReference { FilePath: { } path } && !scannedFiles.Add(path))
					continue;
				if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
					AddForwarders(parents, assembly);
			}
		}

		// Find() compresses paths and so writes to parents; iterate over a snapshot of the keys.
		var keys = parents.Keys.ToList();
		var canonicalByRoot = new Dictionary<TypeKey, string>();
		foreach (var key in keys)
		{
			var root = Find(parents, key);
			if (!canonicalByRoot.TryGetValue(root, out var canonical)
				|| string.CompareOrdinal(key.Assembly, canonical) < 0)
			{
				canonicalByRoot[root] = key.Assembly;
			}
		}

		var canonicalAssemblies = new Dictionary<TypeKey, string>(keys.Count);
		foreach (var key in keys)
			canonicalAssemblies[key] = canonicalByRoot[Find(parents, key)];

		return new TypeForwardingMap(canonicalAssemblies);
	}

	/// <summary>
	/// The canonical assembly name of <paramref name="type"/>: the one shared by all assemblies that forward its
	/// outermost type to one another, or the name of its own assembly when no forwarder mentions it. Null for
	/// types without a containing assembly (error types).
	/// </summary>
	public string? GetCanonicalAssemblyName(INamedTypeSymbol type)
	{
		var outermost = type;
		while (outermost.ContainingType is { } containingType)
			outermost = containingType;

		var assembly = outermost.ContainingAssembly?.Identity.Name;
		if (assembly is null || !_forwardingAssemblies.Contains(assembly))
			return assembly;

		return _canonicalAssemblies.TryGetValue(new TypeKey(assembly, NamespaceName(outermost), outermost.MetadataName), out var canonical)
			? canonical
			: assembly;
	}

	private static void AddForwarders(Dictionary<TypeKey, TypeKey> parents, IAssemblySymbol assembly)
	{
		foreach (var destination in assembly.GetForwardedTypes())
		{
			// Nested types follow their outermost type, which carries the canonical assembly.
			if (destination.ContainingType is not null)
				continue;
			if (destination.ContainingAssembly?.Identity.Name is not { } destinationAssembly)
				continue;

			var ns = NamespaceName(destination);
			Union(parents,
				new TypeKey(assembly.Identity.Name, ns, destination.MetadataName),
				new TypeKey(destinationAssembly, ns, destination.MetadataName));
		}
	}

	private static string NamespaceName(INamedTypeSymbol type)
	{
		return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty;
	}

	private static void Union(Dictionary<TypeKey, TypeKey> parents, in TypeKey a, in TypeKey b)
	{
		var rootA = Find(parents, a);
		var rootB = Find(parents, b);
		if (!rootA.Equals(rootB))
			parents[rootA] = rootB;
	}

	private static TypeKey Find(Dictionary<TypeKey, TypeKey> parents, in TypeKey key)
	{
		if (!parents.TryGetValue(key, out var parent))
		{
			parents[key] = key;
			return key;
		}

		if (parent.Equals(key))
			return key;

		var root = Find(parents, parent);
		parents[key] = root;
		return root;
	}

	// Full metadata name split into namespace and metadata name ("List`1"), which keeps generic arities apart.
	private readonly record struct TypeKey(string Assembly, string Namespace, string MetadataName);
}
