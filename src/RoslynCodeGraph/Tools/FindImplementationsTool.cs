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

        foreach (var target in targetTypes)
        {
            foreach (var candidate in resolver.AllTypes)
            {
                if (SymbolEqualityComparer.Default.Equals(candidate, target))
                    continue;

                bool isMatch = false;

                if (target.TypeKind == TypeKind.Interface)
                {
                    isMatch = candidate.AllInterfaces.Any(i =>
                        SymbolEqualityComparer.Default.Equals(i, target));
                }
                else if (target.TypeKind == TypeKind.Class)
                {
                    var baseType = candidate.BaseType;
                    while (baseType != null)
                    {
                        if (SymbolEqualityComparer.Default.Equals(baseType, target))
                        {
                            isMatch = true;
                            break;
                        }
                        baseType = baseType.BaseType;
                    }
                }

                if (isMatch)
                {
                    var (file, line) = resolver.GetFileAndLine(candidate);
                    var project = resolver.GetProjectName(candidate);
                    var kind = candidate.TypeKind switch
                    {
                        TypeKind.Struct => "struct",
                        TypeKind.Interface => "interface",
                        _ => "class"
                    };
                    results.Add(new SymbolLocation(kind, candidate.ToDisplayString(), file, line, project));
                }
            }
        }

        return results.DistinctBy(r => r.FullName).ToList();
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
        return FindImplementationsLogic.Execute(loaded, resolver, symbol);
    }
}
