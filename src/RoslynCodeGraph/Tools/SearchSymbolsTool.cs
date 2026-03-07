using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class SearchSymbolsLogic
{
    private const int MaxResults = 50;

    public static List<SymbolLocation> Execute(SymbolResolver resolver, string query)
    {
        var results = new List<SymbolLocation>();

        // Search types using pre-built index (substring match on keys)
        foreach (var (simpleName, types) in resolver.TypesBySimpleName)
        {
            if (!simpleName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var type in types)
            {
                var (file, line) = resolver.GetFileAndLine(type);
                if (string.IsNullOrEmpty(file))
                    continue;

                var kind = type.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    TypeKind.Delegate => "delegate",
                    _ => "class"
                };

                var project = resolver.GetProjectName(type);
                results.Add(new SymbolLocation(kind, type.ToDisplayString(), file, line, project));

                if (results.Count >= MaxResults)
                    return results;
            }
        }

        // Search members using pre-built index (substring match on keys)
        foreach (var (memberName, members) in resolver.MembersBySimpleName)
        {
            if (!memberName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var member in members)
            {
                var (file, line) = resolver.GetFileAndLine(member);
                if (string.IsNullOrEmpty(file))
                    continue;

                string? kind = member switch
                {
                    IMethodSymbol m when m.MethodKind == MethodKind.Constructor => "constructor",
                    IMethodSymbol => "method",
                    IPropertySymbol => "property",
                    IFieldSymbol => "field",
                    IEventSymbol => "event",
                    _ => null
                };

                if (kind == null)
                    continue;

                var project = resolver.GetProjectName(member);
                results.Add(new SymbolLocation(kind, member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), file, line, project));

                if (results.Count >= MaxResults)
                    return results;
            }
        }

        return results;
    }
}

[McpServerToolType]
public static class SearchSymbolsTool
{
    [McpServerTool(Name = "search_symbols"),
     Description("Search for types, methods, properties, and fields by name (case-insensitive substring match, max 50 results)")]
    public static List<SymbolLocation> Execute(
        SolutionManager manager,
        [Description("Search query (substring match against symbol names)")] string query)
    {
        manager.EnsureLoaded();
        return SearchSymbolsLogic.Execute(manager.GetResolver(), query);
    }
}
