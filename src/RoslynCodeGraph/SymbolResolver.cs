using Microsoft.CodeAnalysis;

namespace RoslynCodeGraph;

public class SymbolResolver
{
    private readonly LoadedSolution _loaded;
    private readonly List<INamedTypeSymbol> _allTypes;
    private readonly Dictionary<string, List<INamedTypeSymbol>> _typesBySimpleName;
    private readonly Dictionary<string, INamedTypeSymbol> _typesByFullName;
    private readonly Dictionary<string, string> _fileToProjectName;

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
}
