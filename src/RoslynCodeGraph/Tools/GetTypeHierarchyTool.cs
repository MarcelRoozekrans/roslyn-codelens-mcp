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

        // Walk DOWN: find derived types using pre-built index
        var derived = new List<SymbolLocation>();
        var derivedTypes = target.TypeKind == TypeKind.Interface
            ? resolver.GetInterfaceImplementors(target)
            : resolver.GetDerivedTypes(target);
        var seen = new HashSet<string>();

        foreach (var candidate in derivedTypes)
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
            derived.Add(new SymbolLocation(kind, fullName, file, line, project));
        }

        return new TypeHierarchy(
            bases,
            interfaces,
            derived);
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
