using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindImplementationsLogic
{
    public static IReadOnlyList<SymbolLocation> Execute(
        LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
    {
        var targetTypes = source.FindNamedTypes(symbol);
        if (targetTypes.Count == 0)
        {
            var resolved = metadata.Resolve(symbol);
            if (resolved?.Symbol is INamedTypeSymbol nt)
                targetTypes = [nt];
            else
                return [];
        }

        var results = new List<SymbolLocation>();
        // By type identity, not by name: same-named types of different projects are different implementations.
        var seen = new HashSet<INamedTypeSymbol>(source.TypeComparer);

        foreach (var target in targetTypes)
        {
            IReadOnlyList<INamedTypeSymbol> candidates;

            if (target.TypeKind == TypeKind.Interface)
                candidates = source.GetInterfaceImplementors(target);
            else
                candidates = source.GetDerivedTypes(target);

            foreach (var candidate in candidates)
            {
                if (!seen.Add(candidate))
                    continue;

                var fullName = candidate.ToDisplayString();
                var (file, line) = source.GetFileAndLine(candidate);
                var project = source.GetProjectName(candidate);
                var kind = candidate.TypeKind switch
                {
                    TypeKind.Struct => "struct",
                    TypeKind.Interface => "interface",
                    _ => "class"
                };
                results.Add(new SymbolLocation(kind, fullName, file, line, project, source.IsGenerated(file)));
            }
        }

        return results;
    }
}
