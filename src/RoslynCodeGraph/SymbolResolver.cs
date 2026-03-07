using Microsoft.CodeAnalysis;

namespace RoslynCodeGraph;

public class SymbolResolver
{
    private readonly LoadedSolution _loaded;
    private readonly List<INamedTypeSymbol> _allTypes;
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesBySimpleName;
    private readonly Dictionary<string, INamedTypeSymbol> _typesByFullName;
    private readonly Dictionary<string, string> _fileToProjectName;
    private readonly Dictionary<ProjectId, string> _projectIdToName;
    private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _interfaceImplementors;
    private readonly Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> _derivedTypes;
    private readonly Dictionary<string, List<ISymbol>> _membersBySimpleName;
    private readonly Dictionary<string, List<(ISymbol Symbol, AttributeData Attribute)>> _attributeIndex;
    private readonly HashSet<string> _generatedFilePaths;

    public SymbolResolver(LoadedSolution loaded)
    {
        _loaded = loaded;

        // Build cached type lists once
        var allTypes = new List<INamedTypeSymbol>();
        var seen = new HashSet<string>();

        foreach (var compilation in loaded.Compilations.Values)
        {
            foreach (var type in GetAllTypes(compilation.GlobalNamespace))
            {
                var fullName = type.ToDisplayString();
                if (seen.Add(fullName))
                    allTypes.Add(type);
            }
        }

        _allTypes = allTypes;

        // Build lookup dictionaries
        _typesBySimpleName = new Dictionary<string, List<INamedTypeSymbol>>();
        _typesByFullName = new Dictionary<string, INamedTypeSymbol>();

        foreach (var type in _allTypes)
        {
            var fullName = type.ToDisplayString();
            _typesByFullName[fullName] = type;

            if (!_typesBySimpleName.TryGetValue(type.Name, out var list))
            {
                list = new List<INamedTypeSymbol>();
                _typesBySimpleName[type.Name] = list;
            }
            list.Add(type);
        }

        // Build file-to-project lookup
        _fileToProjectName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null)
                    _fileToProjectName[doc.FilePath] = project.Name;
            }
        }

        // Build ProjectId-to-name lookup
        _projectIdToName = new Dictionary<ProjectId, string>();
        foreach (var project in loaded.Solution.Projects)
            _projectIdToName[project.Id] = project.Name;

        // Build reverse inheritance maps
        var comparer = SymbolEqualityComparer.Default;
        _interfaceImplementors = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(comparer);
        _derivedTypes = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(comparer);

        foreach (var type in _allTypes)
        {
            // Index interface implementations
            foreach (var iface in type.AllInterfaces)
            {
                if (!_interfaceImplementors.TryGetValue(iface, out var implList))
                {
                    implList = new List<INamedTypeSymbol>();
                    _interfaceImplementors[iface] = implList;
                }
                implList.Add(type);
            }

            // Index direct base type → derived
            var baseType = type.BaseType;
            while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
            {
                if (!_derivedTypes.TryGetValue(baseType, out var derivedList))
                {
                    derivedList = new List<INamedTypeSymbol>();
                    _derivedTypes[baseType] = derivedList;
                }
                derivedList.Add(type);
                baseType = baseType.BaseType;
            }
        }

        // Build member name index for fast search
        _membersBySimpleName = new Dictionary<string, List<ISymbol>>(StringComparer.OrdinalIgnoreCase);
        // Build attribute index keyed by attribute simple name (without "Attribute" suffix)
        _attributeIndex = new Dictionary<string, List<(ISymbol, AttributeData)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in _allTypes)
        {
            // Index type-level attributes
            IndexAttributes(type);

            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared || string.IsNullOrEmpty(member.Name))
                    continue;

                // Index member-level attributes
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

        // Build generated file path index
        _generatedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var compilation in loaded.Compilations.Values)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var path = tree.FilePath;
                if (IsGeneratedPath(path))
                    _generatedFilePaths.Add(path);
            }
        }
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
    /// All deduplicated types across all compilations, cached at construction.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> AllTypes => _allTypes;

    public List<INamedTypeSymbol> FindNamedTypes(string symbol)
    {
        if (symbol.Contains('.'))
        {
            return _typesByFullName.TryGetValue(symbol, out var type)
                ? [type]
                : [];
        }

        return _typesBySimpleName.TryGetValue(symbol, out var list)
            ? list
            : [];
    }

    public List<IMethodSymbol> FindMethods(string symbol)
    {
        var results = new List<IMethodSymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var methodName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
        {
            results.AddRange(type.GetMembers(methodName).OfType<IMethodSymbol>());
        }

        return results;
    }

    public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            foreach (var t in GetAllNestedTypes(type))
                yield return t;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypes(childNs))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetAllNestedTypes(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var t in GetAllNestedTypes(nested))
                yield return t;
        }
    }

    public Location? GetLocation(ISymbol symbol)
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

    public List<ISymbol> FindMembers(string symbol)
    {
        var results = new List<ISymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var memberName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
        {
            results.AddRange(type.GetMembers(memberName));
        }

        return results;
    }

    public List<ISymbol> FindSymbols(string symbol)
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
    public List<INamedTypeSymbol> GetInterfaceImplementors(INamedTypeSymbol iface)
    {
        return _interfaceImplementors.TryGetValue(iface, out var list) ? list : [];
    }

    /// <summary>
    /// Returns all types that derive from the given type (directly or transitively), using pre-built index.
    /// </summary>
    public List<INamedTypeSymbol> GetDerivedTypes(INamedTypeSymbol baseType)
    {
        return _derivedTypes.TryGetValue(baseType, out var list) ? list : [];
    }

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
        return path.Contains("/obj/")
            || path.Contains("\\obj\\");
    }
}
