using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GoToDefinitionLogic
{
    public static IReadOnlyList<SymbolLocation> Execute(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string symbol)
    {
        var symbols = resolver.FindSymbols(symbol);
        if (symbols.Count > 0)
        {
            var sourceResults = BuildSourceResults(resolver, symbols);
            if (sourceResults.Count > 0)
                return sourceResults;
        }

        // Either no source match, or the matches were metadata-only (no source locations).
        // Try the metadata fallback so callers can still locate the symbol
        // (with File="", Line=0) and see an origin pointing at the assembly.
        var resolved = metadata.Resolve(symbol);
        if (resolved == null)
            return [];

        return
        [
            new SymbolLocation(
                KindOf(resolved.Symbol),
                resolved.Symbol.ToDisplayString(),
                File: "",
                Line: 0,
                Project: "",
                IsGenerated: false,
                Origin: resolved.Origin),
        ];
    }

    private static IReadOnlyList<SymbolLocation> BuildSourceResults(
        SymbolResolver resolver, IReadOnlyList<ISymbol> symbols)
    {
        var results = new List<SymbolLocation>();
        // One result per declaration: same-named types of different projects are distinct declarations.
        var seen = new HashSet<(string Name, string File, int Line)>();

        foreach (var s in symbols)
        {
            var (file, line) = resolver.GetFileAndLine(s);
            if (string.IsNullOrEmpty(file))
                continue;

            var fullName = s.ToDisplayString();
            if (!seen.Add((fullName, file, line)))
                continue;

            var project = resolver.GetProjectName(s);
            results.Add(new SymbolLocation(
                KindOf(s), fullName, file, line, project,
                resolver.IsGenerated(file),
                Origin: MetadataSymbolResolver.SourceOrigin));
        }

        return results;
    }

    private static string KindOf(ISymbol s) => s switch
    {
        INamedTypeSymbol t => t.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => "class",
        },
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "symbol",
    };
}
