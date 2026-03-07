using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindImplementationsLogic
{
    public static List<SymbolLocation> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var targetTypes = resolver.FindNamedTypes(symbol);
        var results = new List<SymbolLocation>();
        var seen = new HashSet<string>();

        foreach (var target in targetTypes)
        {
            List<INamedTypeSymbol> candidates;

            if (target.TypeKind == TypeKind.Interface)
                candidates = resolver.GetInterfaceImplementors(target);
            else
                candidates = resolver.GetDerivedTypes(target);

            foreach (var candidate in candidates)
            {
                var fullName = candidate.ToDisplayString();
                if (!seen.Add(fullName))
                    continue;

                var (file, line) = resolver.GetFileAndLine(candidate);
                var project = resolver.GetProjectName(candidate);
                var kind = candidate.TypeKind switch
                {
                    TypeKind.Struct => "struct",
                    TypeKind.Interface => "interface",
                    _ => "class"
                };
                results.Add(new SymbolLocation(kind, fullName, file, line, project));
            }
        }

        return results;
    }
}

[McpServerToolType]
public static class FindImplementationsTool
{
    [McpServerTool(Name = "find_implementations"),
     Description("Find all classes/structs implementing an interface or extending a class")]
    public static List<SymbolLocation> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        SolutionGuard.EnsureLoaded(loaded);
        return FindImplementationsLogic.Execute(loaded, resolver, symbol);
    }
}
