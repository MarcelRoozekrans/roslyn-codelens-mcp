using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class SearchSymbolsLogic
{
    private const int MaxResults = 50;
    private const int MinMetadataQueryLength = 3;

    public static IReadOnlyList<SymbolLocation> Execute(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string query)
    {
        var results = new List<SymbolLocation>();

        SearchTypes(resolver, query, results);
        if (results.Count < MaxResults)
            SearchMembers(resolver, query, results);

        if (results.Count < MaxResults && query.Length >= MinMetadataQueryLength)
            SearchMetadata(metadata, query, sourceHitCount: results.Count, results);

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
                    _ => "class",
                };

                var project = resolver.GetProjectName(type);
                results.Add(new SymbolLocation(
                    kind, type.ToDisplayString(), file, line, project,
                    IsGenerated: resolver.IsGenerated(file),
                    Origin: MetadataSymbolResolver.SourceOrigin));

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
                    _ => null,
                };

                if (kind == null)
                    continue;

                var project = resolver.GetProjectName(member);
                results.Add(new SymbolLocation(
                    kind, member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    file, line, project,
                    IsGenerated: resolver.IsGenerated(file),
                    Origin: MetadataSymbolResolver.SourceOrigin));

                if (results.Count >= MaxResults)
                    return;
            }
        }
    }

    private static void SearchMetadata(
        MetadataSymbolResolver metadata,
        string query,
        int sourceHitCount,
        List<SymbolLocation> results)
    {
        // Budget heuristic (token cost concern): walking every type in
        // System.Private.CoreLib or the Microsoft.Extensions.* graph is expensive
        // and usually irrelevant — callers who search for a short query rarely
        // want hits from mscorlib. Skip System./Microsoft.* assemblies UNLESS the
        // source pass produced zero hits, in which case the user almost certainly
        // IS looking for a framework type (e.g. "IServiceCollection" lives in
        // Microsoft.Extensions.DependencyInjection.Abstractions — see
        // SearchSymbolsToolTests.Search_MatchesMetadataSymbol for the canonical
        // example).
        var includeBcl = sourceHitCount == 0;

        foreach (var assembly in metadata.EnumerateMetadataAssemblies())
        {
            var name = assembly.Identity.Name;
            if (!includeBcl &&
                (name.StartsWith("System.", StringComparison.Ordinal) ||
                 name.StartsWith("Microsoft.", StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (var type in SymbolResolver.GetAllTypes(assembly.GlobalNamespace))
            {
                if (type.DeclaredAccessibility != Accessibility.Public)
                    continue;

                if (!type.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                var kind = type.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    TypeKind.Delegate => "delegate",
                    _ => "class",
                };

                results.Add(new SymbolLocation(
                    kind, type.ToDisplayString(),
                    File: "", Line: 0, Project: "",
                    IsGenerated: false,
                    Origin: MetadataSymbolResolver.ToOrigin(type)));

                if (results.Count >= MaxResults)
                    return;
            }
        }
    }
}
