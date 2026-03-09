using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens;

public class SymbolResolver
{
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
        _allTypes = CollectAllTypes(loaded);
        (_typesBySimpleName, _typesByFullName) = BuildTypeLookups(_allTypes);
        (_fileToProjectName, _projectIdToName) = BuildProjectLookups(loaded);
        (_interfaceImplementors, _derivedTypes) = BuildInheritanceMaps(_allTypes);

        _membersBySimpleName = new Dictionary<string, List<ISymbol>>(StringComparer.OrdinalIgnoreCase);
        _attributeIndex = new Dictionary<string, List<(ISymbol, AttributeData)>>(StringComparer.OrdinalIgnoreCase);
        BuildMemberAndAttributeIndexes();

        _generatedFilePaths = BuildGeneratedFileIndex(loaded);
    }

    private static List<INamedTypeSymbol> CollectAllTypes(LoadedSolution loaded)
    {
        var allTypes = new List<INamedTypeSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var compilation in loaded.Compilations.Values)
        {
            foreach (var type in GetAllTypes(compilation.GlobalNamespace))
            {
                if (seen.Add(type.ToDisplayString()))
                    allTypes.Add(type);
            }
        }

        return allTypes;
    }

    private static (Dictionary<string, List<INamedTypeSymbol>>, Dictionary<string, INamedTypeSymbol>)
        BuildTypeLookups(List<INamedTypeSymbol> allTypes)
    {
        var bySimple = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
        var byFull = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);

        foreach (ref readonly var type in CollectionsMarshal.AsSpan(allTypes))
        {
            byFull[type.ToDisplayString()] = type;

            if (!bySimple.TryGetValue(type.Name, out var list))
            {
                list = new List<INamedTypeSymbol>();
                bySimple[type.Name] = list;
            }
            list.Add(type);
        }

        return (bySimple, byFull);
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
        }

        return (fileToProject, idToName);
    }

    private static (Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>,
                     Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>)
        BuildInheritanceMaps(List<INamedTypeSymbol> allTypes)
    {
        var comparer = SymbolEqualityComparer.Default;
        var implementors = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(comparer);
        var derived = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(comparer);

        foreach (ref readonly var type in CollectionsMarshal.AsSpan(allTypes))
        {
            foreach (var iface in type.AllInterfaces)
            {
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
    /// All deduplicated types across all compilations, cached at construction.
    /// </summary>
    public IReadOnlyList<INamedTypeSymbol> AllTypes => _allTypes;

    public IReadOnlyList<INamedTypeSymbol> FindNamedTypes(string symbol)
    {
        if (symbol.Contains('.', StringComparison.Ordinal))
        {
            return _typesByFullName.TryGetValue(symbol, out var type)
                ? [type]
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
            results.AddRange(type.GetMembers(methodName).OfType<IMethodSymbol>());
        }

        return results;
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
        {
            foreach (var m in type.GetMembers(memberName))
                results.Add(m);
        }

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
