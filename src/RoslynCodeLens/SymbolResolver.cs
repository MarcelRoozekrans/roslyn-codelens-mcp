using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens;

public class SymbolResolver
{
    private readonly NamedTypeSignatureComparer _typeComparer;
    private readonly SymbolSignatureComparer _symbolComparer;
    private readonly List<INamedTypeSymbol> _allTypes;
    private readonly Dictionary<INamedTypeSymbol, INamedTypeSymbol[]> _typeInstances;
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesBySimpleName;
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesByFullName;
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesByStrippedFullName;
    private readonly Dictionary<string, string> _fileToProjectName;
    private readonly Dictionary<ProjectId, string> _projectIdToName;
    private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _interfaceImplementors;
    private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _derivedTypes;
    private readonly Dictionary<string, List<ISymbol>> _membersBySimpleName;
    private readonly Dictionary<string, List<(ISymbol Symbol, AttributeData Attribute)>> _attributeIndex;
    private readonly HashSet<string> _generatedFilePaths;
    private readonly Dictionary<string, string> _generatedToMarkup;

    public SymbolResolver(LoadedSolution loaded)
    {
        var compilations = CompilationsInStableOrder(loaded);
        _typeComparer = new NamedTypeSignatureComparer(TypeForwardingMap.Build(compilations));
        _symbolComparer = new SymbolSignatureComparer(_typeComparer);

        (_allTypes, _typeInstances) = CollectAllTypes(compilations, _typeComparer);
        (_typesBySimpleName, _typesByFullName, _typesByStrippedFullName) = BuildTypeLookups(_allTypes);
        (_fileToProjectName, _projectIdToName) = BuildProjectLookups(loaded);
        (_interfaceImplementors, _derivedTypes) = BuildInheritanceMaps(_allTypes, _typeComparer);

        _membersBySimpleName = new Dictionary<string, List<ISymbol>>(StringComparer.OrdinalIgnoreCase);
        _attributeIndex = new Dictionary<string, List<(ISymbol, AttributeData)>>(StringComparer.OrdinalIgnoreCase);
        BuildMemberAndAttributeIndexes();

        _generatedFilePaths = BuildGeneratedFileIndex(loaded);
        _generatedToMarkup = Analysis.MarkupDocumentMap.BuildGeneratedToMarkup(loaded);
    }

    // The compilation order decides which instance represents a type and the order of same-named types, so it must
    // not depend on the load. Compilations is keyed by ProjectIds, which are new GUIDs on every load; Solution.Projects
    // follows the load order, which for a solution opened project by project (legacy projects, fallbacks) is the
    // enumeration order of a dictionary keyed by project path, whose string hashes are randomized per process. The
    // project file path (and the name, which tells the target frameworks of one project apart) does not depend on
    // the load.
    private static List<Compilation> CompilationsInStableOrder(LoadedSolution loaded)
    {
        var projects = loaded.Solution.Projects
            .Where(p => loaded.Compilations.ContainsKey(p.Id))
            .OrderBy(p => p.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal);

        var compilations = new List<Compilation>(loaded.Compilations.Count);
        foreach (var project in projects)
            compilations.Add(loaded.Compilations[project.Id]);
        return compilations;
    }

    // Every logical type once, represented by the instance found first. Same-named types of different assemblies
    // stay separate entries unless type forwarding makes them one (see TypeForwardingMap).
    // Per logical type the index also keeps one instance per distinct assembly — a forwarded framework type has
    // one per target framework family — for the tools that must query each of them (see GetInstances).
    private static (List<INamedTypeSymbol>, Dictionary<INamedTypeSymbol, INamedTypeSymbol[]>) CollectAllTypes(
        List<Compilation> compilations, NamedTypeSignatureComparer comparer)
    {
        var allTypes = new List<INamedTypeSymbol>();
        var instances = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(comparer);

        foreach (var compilation in compilations)
            Collect(compilation.GlobalNamespace);

        var typeInstances = new Dictionary<INamedTypeSymbol, INamedTypeSymbol[]>(comparer);
        foreach (var (type, list) in instances)
        {
            if (list.Count > 1)
                typeInstances[type] = list.ToArray();
        }

        return (allTypes, typeInstances);

        void Collect(INamespaceSymbol ns)
        {
            foreach (var type in GetAllTypes(ns))
            {
                if (!instances.TryGetValue(type, out var list))
                {
                    instances[type] = [type];
                    allTypes.Add(type);
                }
                else if (!ContainsAssembly(list, type.ContainingAssembly?.Identity.Name))
                {
                    list.Add(type);
                }
            }
        }
    }

    private static bool ContainsAssembly(List<INamedTypeSymbol> types, string? assemblyName)
    {
        foreach (var type in types)
        {
            if (string.Equals(type.ContainingAssembly?.Identity.Name, assemblyName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Fully qualified name without generic type parameters: renders
    /// "Data.Repository&lt;T&gt;" as "Data.Repository" and nested
    /// "Ns.Outer&lt;T&gt;.Inner" as "Ns.Outer.Inner".
    /// </summary>
    private static readonly SymbolDisplayFormat StrippedGenericsFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    private static (Dictionary<string, List<INamedTypeSymbol>>, Dictionary<string, List<INamedTypeSymbol>>,
                     Dictionary<string, List<INamedTypeSymbol>>)
        BuildTypeLookups(List<INamedTypeSymbol> allTypes)
    {
        var bySimple = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
        var byFull = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
        var byStripped = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);

        foreach (ref readonly var type in CollectionsMarshal.AsSpan(allTypes))
        {
            // Two projects may declare the same full name; both types stay reachable under it.
            var fullName = type.ToDisplayString();
            if (!byFull.TryGetValue(fullName, out var fullList))
            {
                fullList = new List<INamedTypeSymbol>();
                byFull[fullName] = fullList;
            }
            fullList.Add(type);

            // Generic types display with their type parameters ("Data.Repository<T>"),
            // so the documented qualified form "Data.Repository" misses byFull. Index
            // them under the arity-stripped name too; multiple arities share a key and
            // surface as multiple matches for the caller's ambiguity handling.
            var strippedName = type.ToDisplayString(StrippedGenericsFormat);
            if (!string.Equals(strippedName, fullName, StringComparison.Ordinal))
            {
                if (!byStripped.TryGetValue(strippedName, out var strippedList))
                {
                    strippedList = new List<INamedTypeSymbol>();
                    byStripped[strippedName] = strippedList;
                }
                strippedList.Add(type);
            }

            if (!bySimple.TryGetValue(type.Name, out var list))
            {
                list = new List<INamedTypeSymbol>();
                bySimple[type.Name] = list;
            }
            list.Add(type);
        }

        return (bySimple, byFull, byStripped);
    }

    private static (Dictionary<string, string>, Dictionary<ProjectId, string>)
        BuildProjectLookups(LoadedSolution loaded)
    {
        var fileToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idToName = new Dictionary<ProjectId, string>();

        foreach (var project in loaded.Solution.Projects)
        {
            idToName[project.Id] = project.Name;
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null)
                    fileToProject[doc.FilePath] = project.Name;
            }

            // Source-generated documents are not in project.Documents, but their symbols are
            // real and get returned by search_symbols/find_references. Indexing only the
            // on-disk documents left every generated symbol reporting an empty project name
            // — which is most symbols in a Blazor project, where components live in
            // generator output. The compilation is the only place both kinds appear together.
            if (loaded.Compilations.TryGetValue(project.Id, out var compilation))
            {
                foreach (var tree in compilation.SyntaxTrees)
                {
                    if (!string.IsNullOrEmpty(tree.FilePath))
                        fileToProject[tree.FilePath] = project.Name;
                }
            }
        }

        return (fileToProject, idToName);
    }

    private static (Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>,
                     Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>)
        BuildInheritanceMaps(List<INamedTypeSymbol> allTypes, NamedTypeSignatureComparer comparer)
    {
        var implementors = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(comparer);
        var derived = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(comparer);

        foreach (ref readonly var type in CollectionsMarshal.AsSpan(allTypes))
        {
            foreach (var iface in type.AllInterfaces)
            {
                // Skip error symbols (e.g. NoPiaMissingCanonicalTypeSymbol from unresolved
                // embedded-interop types) — they have no real identity and would pollute the index.
                if (iface.TypeKind == TypeKind.Error) continue;
                if (!implementors.TryGetValue(iface, out var implList))
                {
                    implList = new List<INamedTypeSymbol>();
                    implementors[iface] = implList;
                }
                implList.Add(type);
            }

            var baseType = type.BaseType;
            while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
            {
                if (baseType.TypeKind == TypeKind.Error) break;
                if (!derived.TryGetValue(baseType, out var derivedList))
                {
                    derivedList = new List<INamedTypeSymbol>();
                    derived[baseType] = derivedList;
                }
                derivedList.Add(type);
                baseType = baseType.BaseType;
            }
        }

        return (implementors, derived);
    }

    private void BuildMemberAndAttributeIndexes()
    {
        foreach (ref readonly var type in CollectionsMarshal.AsSpan(_allTypes))
        {
            IndexAttributes(type);

            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared || string.IsNullOrEmpty(member.Name))
                    continue;

                IndexAttributes(member);

                if (member is IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
                    continue;

                if (!_membersBySimpleName.TryGetValue(member.Name, out var memberList))
                {
                    memberList = new List<ISymbol>();
                    _membersBySimpleName[member.Name] = memberList;
                }
                memberList.Add(member);
            }
        }
    }

    private static HashSet<string> BuildGeneratedFileIndex(LoadedSolution loaded)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var compilation in loaded.Compilations.Values)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (IsGeneratedPath(tree.FilePath))
                    paths.Add(tree.FilePath);
            }
        }
        return paths;
    }

    private void IndexAttributes(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            if (string.IsNullOrEmpty(attrName))
                continue;

            // Index by full name (e.g., "ObsoleteAttribute")
            if (!_attributeIndex.TryGetValue(attrName, out var list))
            {
                list = new List<(ISymbol, AttributeData)>();
                _attributeIndex[attrName] = list;
            }
            list.Add((symbol, attr));

            // Also index by short name (e.g., "Obsolete")
            if (attrName.EndsWith("Attribute", StringComparison.Ordinal))
            {
                var shortName = attrName[..^"Attribute".Length];
                if (!_attributeIndex.TryGetValue(shortName, out var shortList))
                {
                    shortList = new List<(ISymbol, AttributeData)>();
                    _attributeIndex[shortName] = shortList;
                }
                shortList.Add((symbol, attr));
            }
        }
    }

    /// <summary>
    /// Every logical type across all compilations once, cached at construction. Same-named types of different
    /// assemblies are separate entries unless type forwarding makes them one type.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> AllTypes => _allTypes;

    /// <summary>
    /// Compares types by signature and treats the instances of a forwarded framework type as one type.
    /// </summary>
    internal NamedTypeSignatureComparer TypeComparer => _typeComparer;

    /// <summary>
    /// Compares types and members by signature, with types compared as <see cref="TypeComparer"/> does.
    /// </summary>
    internal SymbolSignatureComparer SymbolComparer => _symbolComparer;

    /// <summary>
    /// The types a name refers to. A full name can match several types when projects declare the same name;
    /// they are returned ordered by the path of their project file.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> FindNamedTypes(string symbol)
    {
        if (symbol.Contains('.', StringComparison.Ordinal))
        {
            if (_typesByFullName.TryGetValue(symbol, out var types))
                return types;

            // Fall back to the arity-stripped index so the documented qualified form
            // ("Data.Repository") reaches generic types ("Data.Repository<T>").
            return _typesByStrippedFullName.TryGetValue(symbol, out var strippedMatches)
                ? strippedMatches
                : [];
        }

        return _typesBySimpleName.TryGetValue(symbol, out var list)
            ? list
            : [];
    }

    public IReadOnlyList<IMethodSymbol> FindMethods(string symbol)
    {
        var results = new List<IMethodSymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var methodName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
        {
            results.AddRange(GetMembersOfAllInstances(type, methodName).OfType<IMethodSymbol>());
        }

        return results;
    }

    public IReadOnlyList<IEventSymbol> FindEvents(string symbol)
    {
        var results = new List<IEventSymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var eventName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
        {
            results.AddRange(GetMembersOfAllInstances(type, eventName).OfType<IEventSymbol>());
        }

        return results;
    }

    // The members of a type merged over its instances: an instance can declare members the representative lacks, e.g.
    // String.Contains(string, StringComparison) exists in System.Runtime of net10.0 but not in mscorlib of net48.
    private IReadOnlyList<ISymbol> GetMembersOfAllInstances(INamedTypeSymbol type, string name)
    {
        var members = type.GetMembers(name);
        if (!_typeInstances.TryGetValue(type, out var instances))
            return members;

        var result = new List<ISymbol>(members);
        foreach (var instance in instances)
        {
            if (SymbolEqualityComparer.Default.Equals(instance, type))
                continue;

            foreach (var member in instance.GetMembers(name))
            {
                if (!result.Contains(member, _symbolComparer))
                    result.Add(member);
            }
        }

        return result;
    }

    public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        return ns.GetTypeMembers().SelectMany(GetAllNestedTypes)
            .Concat(ns.GetNamespaceMembers().SelectMany(GetAllTypes));
    }

    private static IEnumerable<INamedTypeSymbol> GetAllNestedTypes(INamedTypeSymbol type)
    {
        return new[] { type }
            .Concat(type.GetTypeMembers().SelectMany(GetAllNestedTypes));
    }

    public static Location? GetLocation(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(l => l.IsInSource);
    }

    public (string File, int Line) GetFileAndLine(ISymbol symbol)
    {
        var location = GetLocation(symbol);
        if (location == null) return ("", 0);

        var lineSpan = location.GetLineSpan();
        return (lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }

    public string GetProjectName(ISymbol symbol)
    {
        var location = GetLocation(symbol);
        if (location?.SourceTree == null) return "";

        return _fileToProjectName.TryGetValue(location.SourceTree.FilePath, out var name)
            ? name
            : "";
    }

    public string GetProjectName(ProjectId projectId)
    {
        return _projectIdToName.TryGetValue(projectId, out var name) ? name : "";
    }

    public IReadOnlyList<ISymbol> FindMembers(string symbol)
    {
        var results = new List<ISymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var memberName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
            results.AddRange(GetMembersOfAllInstances(type, memberName));

        return results;
    }

    public IReadOnlyList<ISymbol> FindSymbols(string symbol)
    {
        // Try as type first
        var types = FindNamedTypes(symbol);
        if (types.Count > 0)
            return types.Cast<ISymbol>().ToList();

        // Try as Type.Member (methods, properties, fields, events)
        var members = FindMembers(symbol);
        if (members.Count > 0)
            return members;

        return [];
    }

    public IReadOnlyDictionary<string, List<INamedTypeSymbol>> TypesBySimpleName => _typesBySimpleName;
    public IReadOnlyDictionary<string, List<ISymbol>> MembersBySimpleName => _membersBySimpleName;
    public IReadOnlyDictionary<string, List<(ISymbol Symbol, AttributeData Attribute)>> AttributeIndex => _attributeIndex;

    /// <summary>
    /// Returns all types that implement the given interface, using pre-built index.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> GetInterfaceImplementors(INamedTypeSymbol iface)
    {
        return _interfaceImplementors.TryGetValue(iface, out var list) ? list : [];
    }

    /// <summary>
    /// Returns all types that derive from the given type (directly or transitively), using pre-built index.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> GetDerivedTypes(INamedTypeSymbol baseType)
    {
        return _derivedTypes.TryGetValue(baseType, out var list) ? list : [];
    }

    /// <summary>
    /// <paramref name="symbol"/> followed by its instances in the other assemblies of its logical type. A framework
    /// type that type forwarding unites across target frameworks has one instance per assembly (System.IDisposable
    /// in mscorlib, netstandard and System.Runtime); members are mapped to their counterparts by signature. Every
    /// other symbol yields just itself.
    /// </summary>
    /// <remarks>
    /// Roslyn's SymbolFinder matches a symbol only in compilations that can see its assembly: a start symbol from
    /// System.Runtime finds no usage in a net48 project. Tools built on it query every instance and merge the results.
    /// </remarks>
    public IReadOnlyList<ISymbol> GetInstances(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is null
            || !SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition)
            || !_typeInstances.TryGetValue(type, out var instances))
        {
            return [symbol];
        }

        var assembly = type.ContainingAssembly?.Identity.Name;
        var result = new List<ISymbol>(instances.Length) { symbol };
        foreach (var instance in instances)
        {
            if (string.Equals(instance.ContainingAssembly?.Identity.Name, assembly, StringComparison.Ordinal))
                continue;

            if (symbol is INamedTypeSymbol)
            {
                result.Add(instance);
                continue;
            }

            foreach (var member in instance.GetMembers(symbol.Name))
            {
                if (_symbolComparer.Equals(member, symbol))
                {
                    result.Add(member);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The markup file (.razor/.cshtml) a generated document was produced from, or null when the
    /// file is not generator output from markup. Lets results that necessarily point into obj/
    /// name the file the user can actually edit.
    /// </summary>
    public string? GetMarkupSource(string? filePath) =>
        filePath is not null && _generatedToMarkup.TryGetValue(filePath, out var markup) ? markup : null;

    public bool IsGenerated(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return true;
        if (_generatedFilePaths.Contains(filePath))
            return true;
        return IsGeneratedPath(filePath);
    }

    private static bool IsGeneratedPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return true;
        return path.Contains("/obj/", StringComparison.Ordinal)
            || path.Contains("\\obj\\", StringComparison.Ordinal);
    }
}
