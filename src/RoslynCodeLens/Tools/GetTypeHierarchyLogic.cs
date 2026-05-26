using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetTypeHierarchyLogic
{
    /// <summary>
    /// Walks base classes, interfaces, and derived types for a named type. Accepts
    /// both source-defined and metadata types. Derived types are source-only — the
    /// server cannot enumerate all derivations across the ecosystem, so implementors
    /// of a metadata interface will only be listed when they live in the loaded
    /// solution's source.
    /// </summary>
    public static TypeHierarchy Execute(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string symbol)
    {
        INamedTypeSymbol? target = null;
        SymbolOrigin origin = MetadataSymbolResolver.SourceOrigin;

        var types = resolver.FindNamedTypes(symbol);
        target = types.FirstOrDefault(t => t.Locations.Any(l => l.IsInSource));
        if (target == null)
        {
            var resolved = metadata.Resolve(symbol);
            if (resolved?.Symbol is INamedTypeSymbol metadataType)
            {
                target = metadataType;
                origin = resolved.Origin;
            }
        }

        if (target == null)
            throw new McpToolException(ToolErrorCode.SymbolNotFound, $"Symbol '{symbol}' not found.", new { symbol });

        var bases = CollectBaseTypes(target, resolver);
        var interfaces = CollectInterfaces(target, resolver);
        var derived = CollectDerivedTypes(target, resolver);

        return new TypeHierarchy(bases, interfaces, derived, Origin: origin);
    }

    private static List<SymbolLocation> CollectBaseTypes(INamedTypeSymbol target, SymbolResolver resolver)
    {
        var bases = new List<SymbolLocation>();
        var baseType = target.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            bases.Add(BuildLocation(baseType, resolver));
            baseType = baseType.BaseType;
        }
        return bases;
    }

    private static List<SymbolLocation> CollectInterfaces(INamedTypeSymbol target, SymbolResolver resolver)
    {
        var interfaces = new List<SymbolLocation>();
        foreach (var iface in target.AllInterfaces)
        {
            interfaces.Add(BuildLocation(iface, resolver));
        }
        return interfaces;
    }

    private static List<SymbolLocation> CollectDerivedTypes(INamedTypeSymbol target, SymbolResolver resolver)
    {
        var derived = new List<SymbolLocation>();
        var derivedTypes = target.TypeKind == TypeKind.Interface
            ? resolver.GetInterfaceImplementors(target)
            : resolver.GetDerivedTypes(target);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in derivedTypes)
        {
            var fullName = candidate.ToDisplayString();
            if (!seen.Add(fullName))
                continue;

            derived.Add(BuildLocation(candidate, resolver));
        }
        return derived;
    }

    private static SymbolLocation BuildLocation(INamedTypeSymbol type, SymbolResolver resolver)
    {
        var (file, line) = resolver.GetFileAndLine(type);
        var project = resolver.GetProjectName(type);
        var kind = GetTypeKindString(type);
        return new SymbolLocation(
            kind, type.ToDisplayString(), file, line, project,
            IsGenerated: false,
            Origin: MetadataSymbolResolver.ToOrigin(type));
    }

    private static string GetTypeKindString(INamedTypeSymbol type)
    {
        return type.TypeKind switch
        {
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => "class",
        };
    }
}
