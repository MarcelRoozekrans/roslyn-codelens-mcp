using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynCodeLens.Symbols;

/// <summary>
/// Shared grouping of resolver matches into logical targets: all overloads of one method form a single
/// group (same containing type and name); every other symbol is its own group. Types are compared as the
/// resolver's <see cref="SymbolResolver.TypeComparer"/> does, so the instances of one type across
/// compilations (multi-targeting, referencing projects) form one group, while same-named types declared in
/// different projects or assemblies stay apart. Groups that share a source declaration (a file linked into
/// several projects) are merged, because they are one declaration. Used by rename_symbol, change_signature
/// and get_method_source so their ambiguity semantics stay identical.
/// </summary>
internal static class LogicalMemberGroups
{
    private const string SharedFullNameHint =
        "Symbols that share a full name in different projects or assemblies cannot be selected by name.";
    private const string QualifyHint = "Use a more qualified name.";
    private const string MixedHint =
        "Use a more qualified name; symbols that share a full name in different projects or assemblies cannot be selected by name.";

    public static List<IReadOnlyList<ISymbol>> GroupLogicalTargets(IReadOnlyList<ISymbol> symbols, SymbolResolver resolver)
    {
        var groups = symbols
            .GroupBy(s => s, new LogicalTargetComparer(resolver))
            .Select(g => (IReadOnlyList<ISymbol>)g.ToList())
            .ToList();
        return MergeSharedDeclarations(groups);
    }

    /// <summary>
    /// One symbol per source declaration of <paramref name="group"/>: a declaration in a file linked into several
    /// projects appears once per compilation. Symbols without a source declaration are all kept.
    /// </summary>
    /// <param name="group">A logical target as returned by <see cref="GroupLogicalTargets"/>.</param>
    /// <returns>The symbols of <paramref name="group"/> without the further compilations of a declaration.</returns>
    public static IReadOnlyList<ISymbol> DistinctDeclarations(IReadOnlyList<ISymbol> group)
    {
        var seen = new HashSet<DeclarationKey>();
        var keys = new List<DeclarationKey>();
        var distinct = new List<ISymbol>();
        foreach (var symbol in group)
        {
            // Any shared key makes it the same declaration: a partial type can have one part in a linked file and
            // further parts per project, so its compilations need not start at the same part.
            keys.Clear();
            keys.AddRange(DeclarationKeys(symbol));
            if (keys.Count == 0 || !keys.Exists(seen.Contains))
                distinct.Add(symbol);
            seen.UnionWith(keys);
        }

        return distinct;
    }

    /// <summary>
    /// True when every group has the same full name, i.e. the groups differ only in the project or
    /// assembly that declares them.
    /// </summary>
    public static bool ShareOneFullName(IReadOnlyList<IReadOnlyList<ISymbol>> groups)
        => groups.Select(g => FullName(g[0])).Distinct(StringComparer.Ordinal).Count() == 1;

    /// <summary>
    /// Describes the groups of an ambiguous match: one candidate per group, where candidates that share a
    /// full name with another candidate name their project (or assembly), plus the hint for the caller.
    /// </summary>
    public static LogicalAmbiguity DescribeAmbiguity(IReadOnlyList<IReadOnlyList<ISymbol>> groups, SymbolResolver resolver)
    {
        var colliding = groups
            .GroupBy(g => FullName(g[0]), StringComparer.Ordinal)
            .Where(c => c.Count() > 1)
            .Select(c => c.Key)
            .ToHashSet(StringComparer.Ordinal);

        var candidates = groups
            .Select(g => colliding.Contains(FullName(g[0]))
                ? $"{g[0].ToDisplayString()} ({Origin(g[0], resolver)})"
                : g[0].ToDisplayString())
            .ToList();

        var hint = colliding.Count == 0
            ? QualifyHint
            : groups.All(g => colliding.Contains(FullName(g[0]))) ? SharedFullNameHint : MixedHint;
        return new LogicalAmbiguity(candidates, hint);
    }

    // A file linked into several projects is compiled into each of their assemblies, so the type comparer sees one
    // type per assembly. Groups that share a declaration are one target; the first group of a merge keeps its place.
    private static List<IReadOnlyList<ISymbol>> MergeSharedDeclarations(List<IReadOnlyList<ISymbol>> groups)
    {
        var root = Enumerable.Range(0, groups.Count).ToArray();
        var owner = new Dictionary<DeclarationKey, int>();
        for (var i = 0; i < groups.Count; i++)
        {
            foreach (var symbol in groups[i])
            {
                foreach (var key in DeclarationKeys(symbol))
                {
                    if (!owner.TryAdd(key, i))
                        Union(owner[key], i);
                }
            }
        }

        return groups
            .Select((group, index) => (Group: group, Root: Find(index)))
            .GroupBy(x => x.Root)
            .Select(merged => (IReadOnlyList<ISymbol>)merged.SelectMany(x => x.Group).ToList())
            .ToList();

        int Find(int i)
        {
            while (root[i] != i)
                i = root[i] = root[root[i]];
            return i;
        }

        void Union(int a, int b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra != rb)
                root[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }

    // Documents without a file path (in-memory workspaces) cannot be told apart, so they never count as shared. A
    // declared symbol owns its location; synthesized members share theirs (both Equals overloads of a record point at
    // the record's name), so they also carry their metadata signature. Display strings would not do: the same
    // declaration displays differently per compilation (IntPtr is nint on runtimes with a numeric IntPtr).
    private static IEnumerable<DeclarationKey> DeclarationKeys(ISymbol symbol)
    {
        var synthesized = symbol.IsImplicitlyDeclared ? MetadataSignature(symbol) : "";
        return symbol.Locations
            .Where(l => l.IsInSource && !string.IsNullOrEmpty(l.SourceTree?.FilePath))
            .Select(l => new DeclarationKey(l.SourceTree!.FilePath, l.SourceSpan, synthesized));
    }

    private static string MetadataSignature(ISymbol symbol) => symbol is IMethodSymbol method
        ? $"{method.MetadataName}({string.Join(",", method.Parameters.Select(p => p.Type.MetadataName))})"
        : symbol.MetadataName;

    // The name a caller can type: containing type + name for methods (shared by all overloads), the
    // display string otherwise. Neither contains the project or assembly.
    private static string FullName(ISymbol symbol) => symbol is IMethodSymbol { ContainingType: { } type } method
        ? $"{type.ToDisplayString()}.{method.Name}"
        : symbol.ToDisplayString();

    private static string Origin(ISymbol symbol, SymbolResolver resolver)
    {
        var project = resolver.GetProjectName(symbol);
        return project.Length > 0 ? $"project {project}" : $"assembly {symbol.ContainingAssembly?.Name}";
    }

    private readonly record struct DeclarationKey(string FilePath, TextSpan Span, string SynthesizedSignature);

    private sealed class LogicalTargetComparer(SymbolResolver resolver) : IEqualityComparer<ISymbol>
    {
        public bool Equals(ISymbol? x, ISymbol? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            if (x is IMethodSymbol { ContainingType: { } xType } xMethod && y is IMethodSymbol { ContainingType: { } yType } yMethod)
            {
                return string.Equals(xMethod.Name, yMethod.Name, StringComparison.Ordinal)
                    && resolver.TypeComparer.Equals(xType, yType);
            }
            if (x is IMethodSymbol || y is IMethodSymbol) return false;
            return resolver.SymbolComparer.Equals(x, y);
        }

        public int GetHashCode(ISymbol obj) => obj is IMethodSymbol { ContainingType: { } type } method
            ? HashCode.Combine(StringComparer.Ordinal.GetHashCode(method.Name), resolver.TypeComparer.GetHashCode(type))
            : resolver.SymbolComparer.GetHashCode(obj);
    }
}