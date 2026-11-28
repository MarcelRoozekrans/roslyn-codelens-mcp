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
        return MergeSharedDeclarations(groups, resolver);
    }

    /// <summary>
    /// One symbol per source declaration of <paramref name="symbols"/>: a declaration in a file linked into several
    /// projects appears once per compilation. Symbols without a source declaration are all kept.
    /// </summary>
    /// <param name="symbols">A logical target as returned by <see cref="GroupLogicalTargets"/>, or the constructors
    /// of its types.</param>
    /// <param name="resolver">The resolver that tells project documents from generated ones, which only the
    /// compilations of one assembly share.</param>
    /// <returns>The first symbol of each declaration, in the order of <paramref name="symbols"/>.</returns>
    public static IReadOnlyList<ISymbol> DistinctDeclarations(IReadOnlyList<ISymbol> symbols, SymbolResolver resolver)
    {
        // Any shared key makes it the same declaration: a partial type can have one part in a linked file and further
        // parts per project, so its compilations need not share a part directly.
        return ConnectedComponents(symbols.Select(s => DeclarationKeys(s, resolver)).ToList())
            .Select(component => symbols[component[0]])
            .ToList();
    }

    /// <summary>
    /// True when every symbol of the groups has the same full name, i.e. the groups differ only in the project or
    /// assembly that declares them.
    /// </summary>
    public static bool ShareOneFullName(IReadOnlyList<IReadOnlyList<ISymbol>> groups)
        => groups.SelectMany(g => g).Select(FullName).Distinct(StringComparer.Ordinal).Count() == 1;

    /// <summary>
    /// Describes the groups of an ambiguous match: one candidate per group, where candidates whose full name also
    /// matches another candidate name their project (or assembly), plus the hint for the caller.
    /// </summary>
    /// <remarks>
    /// A group whose declarations have several full names (a linked file compiled into different namespaces) is listed
    /// under the first full name that no other group has, so the caller can select it by that name.
    /// </remarks>
    public static LogicalAmbiguity DescribeAmbiguity(IReadOnlyList<IReadOnlyList<ISymbol>> groups, SymbolResolver resolver)
    {
        var representatives = groups.Select((_, i) => Representative(groups, i)).ToList();

        var candidates = representatives
            .Select((r, i) => r.IsUnique
                ? r.Symbol.ToDisplayString()
                : $"{r.Symbol.ToDisplayString()} ({Origin(groups[i], resolver)})")
            .ToList();

        var hint = representatives.All(r => r.IsUnique)
            ? QualifyHint
            : representatives.Any(r => r.IsUnique) ? MixedHint : SharedFullNameHint;
        return new LogicalAmbiguity(candidates, hint);
    }

    // A file linked into several projects is compiled into each of their assemblies, so the type comparer sees one
    // type per assembly. Groups that share a declaration are one target; the first group of a merge keeps its place.
    private static List<IReadOnlyList<ISymbol>> MergeSharedDeclarations(List<IReadOnlyList<ISymbol>> groups, SymbolResolver resolver)
    {
        return ConnectedComponents(groups.Select(g => g.SelectMany(s => DeclarationKeys(s, resolver))).ToList())
            .Select(component => (IReadOnlyList<ISymbol>)component.SelectMany(i => groups[i]).ToList())
            .ToList();
    }

    // The symbol that stands for the group at index: the first one whose full name no other group has (IsUnique), else
    // the first symbol. A name that any symbol of another group has would select that group too.
    private static (ISymbol Symbol, bool IsUnique) Representative(IReadOnlyList<IReadOnlyList<ISymbol>> groups, int index)
    {
        var otherNames = groups
            .Where((_, i) => i != index)
            .SelectMany(g => g)
            .Select(FullName)
            .ToHashSet(StringComparer.Ordinal);
        var unique = groups[index].FirstOrDefault(s => !otherNames.Contains(FullName(s)));
        return unique is null ? (groups[index][0], false) : (unique, true);
    }

    // The items connected by shared keys, directly or through further items. Components are ordered by their first item,
    // and each lists its item indexes in ascending order; an item without keys is a component of its own.
    private static List<List<int>> ConnectedComponents(IReadOnlyList<IEnumerable<DeclarationKey>> keysPerItem)
    {
        var root = Enumerable.Range(0, keysPerItem.Count).ToArray();
        var owner = new Dictionary<DeclarationKey, int>();
        for (var i = 0; i < keysPerItem.Count; i++)
        {
            foreach (var key in keysPerItem[i])
            {
                if (!owner.TryAdd(key, i))
                    Union(owner[key], i);
            }
        }

        return Enumerable.Range(0, keysPerItem.Count)
            .GroupBy(Find)
            .Select(component => component.ToList())
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
    // generated document is shared only within its assembly: the compilations of a multi-targeted project generate the
    // same document, but two projects can generate the same path and span without sharing anything. A declared symbol
    // owns its location; synthesized members share theirs (both Equals overloads of a record point at the record's
    // name), so they also carry their documentation ID, whose types are namespace-qualified (a record named Object) and
    // which is the same in every compilation of a declaration. Display strings would not do: IntPtr is nint on runtimes
    // with a numeric IntPtr. A partial method, property or event is listed as its definition part, whose locations leave
    // out the implementation part's, so those are added: a definition per project with a linked implementation is one
    // target.
    private static IEnumerable<DeclarationKey> DeclarationKeys(ISymbol symbol, SymbolResolver resolver)
    {
        var synthesized = symbol.IsImplicitlyDeclared
            ? DocumentationCommentId.CreateDeclarationId(symbol) ?? symbol.MetadataName
            : "";
        var implementation = symbol switch
        {
            IMethodSymbol method => method.PartialImplementationPart,
            IPropertySymbol property => (ISymbol?)property.PartialImplementationPart,
            IEventSymbol @event => @event.PartialImplementationPart,
            _ => null,
        };
        return symbol.Locations.Concat(implementation?.Locations ?? [])
            .Where(l => l.IsInSource && !string.IsNullOrEmpty(l.SourceTree?.FilePath))
            .Select(l => new DeclarationKey(
                l.SourceTree!.FilePath,
                l.SourceSpan,
                synthesized,
                resolver.IsProjectDocument(symbol, l.SourceTree.FilePath) ? "" : symbol.ContainingAssembly?.Name ?? ""));
    }

    // The name a caller can type: containing type + name for methods (shared by all overloads), the
    // display string otherwise. Neither contains the project or assembly.
    private static string FullName(ISymbol symbol) => symbol is IMethodSymbol { ContainingType: { } type } method
        ? $"{type.ToDisplayString()}.{method.Name}"
        : symbol.ToDisplayString();

    // The projects whose compilations declare the group, in group order. A file path cannot tell them: a linked file
    // belongs to several projects, and generators can give their documents the same path in every project.
    private static string Origin(IReadOnlyList<ISymbol> group, SymbolResolver resolver)
    {
        var projects = group
            .Select(resolver.GetDeclaringProjectName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return projects.Count switch
        {
            0 => $"assembly {group[0].ContainingAssembly?.Name}",
            1 => $"project {projects[0]}",
            _ => $"projects {string.Join(", ", projects)}",
        };
    }

    // GeneratedIn is the assembly of a generated document and empty for a project document, which a link can share. Paths
    // compare as the file system does: on Windows, links that differ only in casing name one file. Generated paths need no
    // exception, because a generator's hint names are unique regardless of casing.
    private readonly record struct DeclarationKey(string FilePath, TextSpan Span, string SynthesizedSignature, string GeneratedIn)
    {
        public bool Equals(DeclarationKey other)
            => SolutionLoader.PathComparer.Equals(FilePath, other.FilePath)
                && Span == other.Span
                && SynthesizedSignature == other.SynthesizedSignature
                && GeneratedIn == other.GeneratedIn;

        public override int GetHashCode()
            => HashCode.Combine(SolutionLoader.PathComparer.GetHashCode(FilePath), Span, SynthesizedSignature, GeneratedIn);
    }

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