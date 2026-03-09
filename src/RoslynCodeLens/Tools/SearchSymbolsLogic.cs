using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class SearchSymbolsLogic
{
    private const int MaxResults = 50;

    public static IReadOnlyList<SymbolLocation> Execute(SymbolResolver resolver, string query)
    {
        var results = new List<SymbolLocation>();

        SearchTypes(resolver, query, results);
        if (results.Count < MaxResults)
            SearchMembers(resolver, query, results);

        return results;
    }

    private static void SearchTypes(SymbolResolver resolver, string query, List<SymbolLocation> results)
    {
        foreach (var (simpleName, types) in resolver.TypesBySimpleName)
        {
            if (!simpleName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (ref readonly var type in CollectionsMarshal.AsSpan(types))
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
                    return;
            }
        }
    }

    private static void SearchMembers(SymbolResolver resolver, string query, List<SymbolLocation> results)
    {
        foreach (var (memberName, members) in resolver.MembersBySimpleName)
        {
            if (!memberName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (ref readonly var member in CollectionsMarshal.AsSpan(members))
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
                    return;
            }
        }
    }
}
