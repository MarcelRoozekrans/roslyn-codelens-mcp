using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindAttributeUsagesLogic
{
    public static List<AttributeUsageInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string attribute)
    {
        // Use pre-built attribute index for O(1) lookup by name
        if (!resolver.AttributeIndex.TryGetValue(attribute, out var entries))
            return [];

        var results = new List<AttributeUsageInfo>();

        foreach (var (symbol, attr) in entries)
        {
            var (file, line) = resolver.GetFileAndLine(symbol);
            if (string.IsNullOrEmpty(file))
                continue;

            var project = resolver.GetProjectName(symbol);

            var targetKind = symbol switch
            {
                INamedTypeSymbol t => t.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    _ => "class"
                },
                IMethodSymbol m when m.MethodKind == MethodKind.Constructor => "constructor",
                IMethodSymbol => "method",
                IPropertySymbol => "property",
                IFieldSymbol => "field",
                IEventSymbol => "event",
                _ => "member"
            };

            var targetName = symbol is INamedTypeSymbol
                ? symbol.ToDisplayString()
                : symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            results.Add(new AttributeUsageInfo(
                attr.AttributeClass?.Name ?? attribute,
                targetKind, targetName, file, line, project));
        }

        return results;
    }
}

[McpServerToolType]
public static class FindAttributeUsagesTool
{
    [McpServerTool(Name = "find_attribute_usages"),
     Description("Find all types and members decorated with a specific attribute (e.g., Obsolete, Authorize, Serializable)")]
    public static List<AttributeUsageInfo> Execute(
        SolutionManager manager,
        [Description("Attribute name to search for (with or without 'Attribute' suffix)")] string attribute)
    {
        manager.EnsureLoaded();
        return FindAttributeUsagesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), attribute);
    }
}
