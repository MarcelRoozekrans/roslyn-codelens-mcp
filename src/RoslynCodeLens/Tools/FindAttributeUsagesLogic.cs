using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindAttributeUsagesLogic
{
    public static IReadOnlyList<AttributeUsageInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string attribute)
    {
        // Fast path: the pre-built attribute index is keyed by simple name
        // (e.g. "Obsolete", "ObsoleteAttribute"), so matches land here for the
        // common case of callers typing a short attribute name.
        if (resolver.AttributeIndex.TryGetValue(attribute, out var entries))
            return BuildResults(resolver, entries, fallbackAttributeName: attribute);

        // Fallback: the caller may have passed a fully-qualified attribute
        // (e.g. "System.ObsoleteAttribute") that the simple-name index does not
        // reach. Resolve the attribute types — several projects may declare the
        // same full name — and then scan the existing index values for usages
        // whose AttributeClass matches one of them.
        var attributeTypes = resolver.FindNamedTypes(attribute);
        if (attributeTypes.Count == 0)
        {
            if (metadata.Resolve(attribute)?.Symbol is not INamedTypeSymbol resolved)
                return [];
            attributeTypes = [resolved];
        }

        var candidates = new HashSet<INamedTypeSymbol>(attributeTypes, resolver.TypeComparer);
        var matches = new List<(ISymbol Symbol, AttributeData Attribute)>();
        foreach (var list in resolver.AttributeIndex.Values)
        {
            foreach (ref readonly var entry in CollectionsMarshal.AsSpan(list))
            {
                if (entry.Attribute.AttributeClass is { } attributeClass && candidates.Contains(attributeClass))
                    matches.Add(entry);
            }
        }

        return BuildResults(resolver, matches, fallbackAttributeName: attributeTypes[0].Name);
    }

    private static IReadOnlyList<AttributeUsageInfo> BuildResults(
        SymbolResolver resolver,
        IReadOnlyList<(ISymbol Symbol, AttributeData Attribute)> entries,
        string fallbackAttributeName)
    {
        var results = new List<AttributeUsageInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

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
                    _ => "class",
                },
                IMethodSymbol m when m.MethodKind == MethodKind.Constructor => "constructor",
                IMethodSymbol => "method",
                IPropertySymbol => "property",
                IFieldSymbol => "field",
                IEventSymbol => "event",
                _ => "member",
            };

            var targetName = symbol.ToDisplayString();

            // Deduplicate: the attribute index stores each usage twice (once keyed
            // by "Obsolete" and once by "ObsoleteAttribute"), and the metadata
            // fallback concatenates all index lists.
            var dedupKey = $"{targetName}|{file}|{line}";
            if (!seen.Add(dedupKey))
                continue;

            results.Add(new AttributeUsageInfo(
                attr.AttributeClass?.Name ?? fallbackAttributeName,
                targetKind, targetName, file, line, project));
        }

        return results;
    }
}
