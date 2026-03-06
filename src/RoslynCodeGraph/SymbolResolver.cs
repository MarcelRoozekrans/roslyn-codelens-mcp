using Microsoft.CodeAnalysis;

namespace RoslynCodeGraph;

public class SymbolResolver
{
    private readonly LoadedSolution _loaded;

    public SymbolResolver(LoadedSolution loaded)
    {
        _loaded = loaded;
    }

    public List<INamedTypeSymbol> FindNamedTypes(string symbol)
    {
        var results = new List<INamedTypeSymbol>();
        var hasDot = symbol.Contains('.');

        foreach (var compilation in _loaded.Compilations.Values)
        {
            foreach (var type in GetAllTypes(compilation.GlobalNamespace))
            {
                if (hasDot)
                {
                    if (type.ToDisplayString() == symbol)
                        results.Add(type);
                }
                else
                {
                    if (type.Name == symbol)
                        results.Add(type);
                }
            }
        }

        return results.DistinctBy(t => t.ToDisplayString()).ToList();
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

        var filePath = location.SourceTree.FilePath;
        foreach (var project in _loaded.Solution.Projects)
        {
            if (project.Documents.Any(d => d.FilePath == filePath))
                return project.Name;
        }
        return "";
    }
}
