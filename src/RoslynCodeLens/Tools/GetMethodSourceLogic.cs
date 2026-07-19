using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetMethodSourceLogic
{
    private const string MetadataNote =
        "member is defined in metadata — use peek_il or inspect_external_assembly";
    private const string CompilerGeneratedCtorNote =
        "type exists; its only constructors are compiler-generated";
    private const string WholeTypeNote =
        "whole types are not returned — use get_type_overview or Read";

    public static IReadOnlyList<MemberSourceInfo> Execute(
        SymbolResolver resolver, MetadataSymbolResolver metadata, IReadOnlyList<string> symbols)
    {
        if (symbols.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "symbols must contain at least one member name.",
                new { symbols });
        }

        var items = new List<MemberSourceInfo>();
        foreach (var requested in symbols)
            items.AddRange(ResolveOne(resolver, metadata, requested));
        return items;
    }

    private static IEnumerable<MemberSourceInfo> ResolveOne(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string requested)
    {
        // Constructor request form: "Ns.Type.Type" (member segment == type simple name).
        // Must run before FindSymbols — constructors are named ".ctor"/".cctor", so the
        // member index never matches them under the type's name.
        var lastDot = requested.LastIndexOf('.');
        if (lastDot > 0)
        {
            var typePart = requested[..lastDot];
            var memberPart = requested[(lastDot + 1)..];
            if (string.Equals(typePart.Split('.')[^1], memberPart, StringComparison.Ordinal))
            {
                var ctorItems = CtorItems(resolver, requested, typePart).ToList();
                if (ctorItems.Count > 0)
                    return ctorItems;
            }
        }

        var matches = resolver.FindSymbols(requested);
        if (matches.Count == 0)
            matches = FindExplicitImplementations(resolver, requested);
        if (matches.Count == 0)
        {
            var resolved = metadata.Resolve(requested);
            if (resolved != null)
                return [NotInSource(requested, resolved.Symbol)];
            return [new MemberSourceInfo(requested, "notFound", null, null, null, null, null, null, null)];
        }

        // Overloads of one method are ONE logical request; distinct symbols are ambiguity.
        var groups = LogicalMemberGroups.GroupLogicalTargets(matches);
        if (groups.Count > 1)
        {
            return [new MemberSourceInfo(requested, "ambiguous", null, null, null, null, null, null, null,
                groups.Select(g => g.First().ToDisplayString()).ToList())];
        }

        // A logical group with no source-backed symbol is ONE metadata answer for
        // the request — not one stub per overload. In a mixed group (some source,
        // some metadata overloads) the metadata-only overloads are simply skipped.
        var sourceBacked = groups[0]
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .ToList();
        if (sourceBacked.Count == 0)
            return [NotInSource(requested, groups[0].First())];

        return sourceBacked.SelectMany(s => Items(resolver, requested, s));
    }

    /// <summary>
    /// Explicit interface implementations are named after the interface
    /// ("System.IDisposable.Dispose"), so a "Widget.Dispose" request misses the
    /// member index. Resolve the type part and scan its members for names ending
    /// in ".Dispose" (exact matches are already covered by FindSymbols).
    /// </summary>
    private static IReadOnlyList<ISymbol> FindExplicitImplementations(
        SymbolResolver resolver, string requested)
    {
        var lastDot = requested.LastIndexOf('.');
        if (lastDot <= 0)
            return [];

        var typeName = requested[..lastDot];
        var memberSuffix = requested[lastDot..]; // "." + member part

        var results = new List<ISymbol>();
        foreach (var type in resolver.FindNamedTypes(typeName))
        {
            foreach (var member in type.GetMembers())
            {
                if (member.Name.EndsWith(memberSuffix, StringComparison.Ordinal))
                    results.Add(member);
            }
        }
        return results;
    }

    private static IEnumerable<MemberSourceInfo> CtorItems(
        SymbolResolver resolver, string requested, string typeName)
    {
        var types = resolver.FindSymbols(typeName)
            .OfType<INamedTypeSymbol>()
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<INamedTypeSymbol>()
            .ToList();

        // No such type — this wasn't a ctor request after all; the caller falls
        // through to normal member resolution.
        if (types.Count == 0)
            yield break;

        // Multiple distinct types share the simple name: merging their ctors as
        // "ok" would silently interleave unrelated constructors. Report ambiguity,
        // consistent with the member path.
        if (types.Count > 1)
        {
            yield return new MemberSourceInfo(requested, "ambiguous", null, null,
                null, null, null, null, null,
                types.Select(t => t.ToDisplayString()).ToList());
            yield break;
        }

        var type = types[0];
        var emitted = false;
        foreach (var ctor in type.InstanceConstructors.Concat(type.StaticConstructors))
        {
            // Only source-backed constructors: implicit default (and other
            // compiler-generated) ctors have no declaration to show — skip them
            // rather than emitting metadata items for a source type.
            if (ctor.IsImplicitlyDeclared || ctor.DeclaringSyntaxReferences.Length == 0)
                continue;

            foreach (var item in Items(resolver, requested, ctor))
            {
                emitted = true;
                yield return item;
            }
        }
        if (emitted)
            yield break;

        // The type exists but yielded no source-backed ctors — say why instead of
        // a bare notFound.
        if (!type.Locations.Any(l => l.IsInSource))
        {
            yield return new MemberSourceInfo(requested, "metadata", type.ToDisplayString(),
                "constructor", null, null, null, null, null, null,
                MetadataSymbolResolver.ToOrigin(type), MetadataNote);
        }
        else
        {
            yield return new MemberSourceInfo(requested, "notFound", type.ToDisplayString(),
                null, null, null, null, null, null, null, null, CompilerGeneratedCtorNote);
        }
    }

    private static IEnumerable<MemberSourceInfo> Items(
        SymbolResolver resolver, string requested, ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol)
        {
            yield return WholeType(requested, symbol);
            yield break;
        }

        var refs = DeclarationReferences(symbol);
        if (refs.Count == 0 || !symbol.Locations.Any(l => l.IsInSource))
        {
            yield return NotInSource(requested, symbol);
            yield break;
        }

        foreach (var reference in refs)
        {
            var node = reference.GetSyntax();
            // Fields/events resolve to the variable declarator; the useful source is
            // the whole declaration statement (modifiers, type, initializer).
            if (node is VariableDeclaratorSyntax declarator)
                node = declarator.Parent?.Parent ?? node;

            // Implicit accessors etc. — climb to the member declaration.
            while (node is not MemberDeclarationSyntax && node.Parent != null)
                node = node.Parent;

            var span = node.GetLocation().GetLineSpan();
            var (source, skippedLeadingLines) = ExtractSource(node);
            // StartLine must be the first line actually present in Source: the
            // node's FullSpan start (leading trivia incl. XML docs/attributes)
            // plus the blank lines ExtractSource trimmed. EndLine is the last
            // content line, which is the last token's line (trailing trivia never
            // spills onto a further line after TrimEnd).
            var startLine = node.SyntaxTree.GetLineSpan(node.FullSpan)
                .StartLinePosition.Line + skippedLeadingLines + 1;
            yield return new MemberSourceInfo(
                requested, "ok", symbol.ToDisplayString(), KindOf(symbol),
                span.Path, startLine, span.EndLinePosition.Line + 1,
                source,
                resolver.GetProjectName(symbol));
        }
    }

    /// <summary>
    /// All declaration parts of a member. Partial methods and partial properties
    /// (C# 13) surface as a single symbol (the definition part) with the implementation
    /// as a separate linked symbol, each carrying only its own syntax reference —
    /// merge both so every part yields an item.
    /// </summary>
    private static IReadOnlyList<SyntaxReference> DeclarationReferences(ISymbol symbol)
    {
        IEnumerable<SyntaxReference> refs = symbol.DeclaringSyntaxReferences;
        if (symbol is IMethodSymbol method)
        {
            if (method.PartialImplementationPart is { } impl && !SymbolEqualityComparer.Default.Equals(impl, symbol))
                refs = refs.Concat(impl.DeclaringSyntaxReferences);
            else if (method.PartialDefinitionPart is { } def && !SymbolEqualityComparer.Default.Equals(def, symbol))
                refs = def.DeclaringSyntaxReferences.Concat(refs);
        }
        else if (symbol is IPropertySymbol property)
        {
            if (property.PartialImplementationPart is { } impl && !SymbolEqualityComparer.Default.Equals(impl, symbol))
                refs = refs.Concat(impl.DeclaringSyntaxReferences);
            else if (property.PartialDefinitionPart is { } def && !SymbolEqualityComparer.Default.Equals(def, symbol))
                refs = def.DeclaringSyntaxReferences.Concat(refs);
        }

        // Defensive dedupe: if a resolver ever hands back both partial-part symbols,
        // the merged list would repeat a declaration.
        return refs
            .DistinctBy(r => (r.SyntaxTree, r.Span))
            .ToList();
    }

    /// <summary>
    /// Trivia decision: Source starts at the first content line (XML doc comment,
    /// attribute, or modifier). Leading blank lines are trimmed, but the first content
    /// line keeps its original indentation; trailing whitespace/newlines are trimmed.
    /// Also returns how many leading whitespace-only lines were trimmed so the caller
    /// can report a StartLine that matches the first line actually emitted.
    /// </summary>
    private static (string Source, int SkippedLeadingLines) ExtractSource(SyntaxNode node)
    {
        var text = node.ToFullString();
        var lines = text.Split('\n');
        var first = 0;
        while (first < lines.Length && string.IsNullOrWhiteSpace(lines[first]))
            first++;
        return (string.Join('\n', lines.Skip(first)).TrimEnd('\r', '\n', ' ', '\t'), first);
    }

    private static MemberSourceInfo NotInSource(string requested, ISymbol symbol)
        // A TYPE that only resolves via metadata (e.g. the backtick metadata-name
        // form) is still a whole type — not a kind='method' metadata member.
        => symbol is INamedTypeSymbol
            ? WholeType(requested, symbol)
            : new(requested, "metadata", symbol.ToDisplayString(), KindOf(symbol),
                null, null, null, null, null, null,
                MetadataSymbolResolver.ToOrigin(symbol), MetadataNote);

    private static MemberSourceInfo WholeType(string requested, ISymbol type)
        => new(requested, "unsupportedKind", type.ToDisplayString(), null,
            null, null, null, null, null, null, null, WholeTypeNote);

    private static string KindOf(ISymbol s) => s switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "method",
    };
}
