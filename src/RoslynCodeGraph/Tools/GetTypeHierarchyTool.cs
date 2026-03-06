using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetTypeHierarchyLogic
{
    public static TypeHierarchy? Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var types = resolver.FindNamedTypes(symbol);
        if (types.Count == 0)
            return null;

        var target = types[0];

        // Walk UP: base type chain (skip System.Object)
        var bases = new List<SymbolLocation>();
        var baseType = target.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            var (file, line) = resolver.GetFileAndLine(baseType);
            var project = resolver.GetProjectName(baseType);
            var kind = baseType.TypeKind switch
            {
                TypeKind.Struct => "struct",
                TypeKind.Interface => "interface",
                _ => "class"
            };
            bases.Add(new SymbolLocation(kind, baseType.ToDisplayString(), file, line, project));
            baseType = baseType.BaseType;
        }

        // Collect interfaces
        var interfaces = new List<SymbolLocation>();
        foreach (var iface in target.AllInterfaces)
        {
            var (file, line) = resolver.GetFileAndLine(iface);
            var project = resolver.GetProjectName(iface);
            interfaces.Add(new SymbolLocation("interface", iface.ToDisplayString(), file, line, project));
        }

        // Walk DOWN: find derived types
        var derived = new List<SymbolLocation>();
        foreach (var candidate in resolver.AllTypes)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, target))
                continue;

            var candidateBase = candidate.BaseType;
            while (candidateBase != null)
            {
                if (SymbolEqualityComparer.Default.Equals(candidateBase, target))
                {
                    var (file, line) = resolver.GetFileAndLine(candidate);
                    var project = resolver.GetProjectName(candidate);
                    var kind = candidate.TypeKind switch
                    {
                        TypeKind.Struct => "struct",
                        TypeKind.Interface => "interface",
                        _ => "class"
                    };
                    derived.Add(new SymbolLocation(kind, candidate.ToDisplayString(), file, line, project));
                    break;
                }
                candidateBase = candidateBase.BaseType;
            }
        }

        return new TypeHierarchy(
            bases,
            interfaces,
            derived.DistinctBy(d => d.FullName).ToList());
    }
}

[McpServerToolType]
public static class GetTypeHierarchyTool
{
    [McpServerTool(Name = "get_type_hierarchy"),
     Description("Walk up (base classes, interfaces) and down (derived types) from a type")]
    public static TypeHierarchy? Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        return GetTypeHierarchyLogic.Execute(loaded, resolver, symbol);
    }
}
