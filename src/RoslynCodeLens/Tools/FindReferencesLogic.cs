using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindReferencesLogic
{
    public static IReadOnlyList<SymbolReference> Execute(
        LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
    {
        var targets = source.FindSymbols(symbol);
        if (targets.Count == 0)
        {
            var resolved = metadata.Resolve(symbol);
            if (resolved == null)
                return [];
            targets = [resolved.Symbol];
        }

        return ScanForReferences(loaded, source, targets);
    }

    private static List<SymbolReference> ScanForReferences(
        LoadedSolution loaded, SymbolResolver resolver, IReadOnlyList<ISymbol> targets)
    {
        var results = new List<SymbolReference>();
        var seen = new HashSet<(string, int)>();

        foreach (var target in targets)
        {
            // Roslyn's SymbolFinder handles cross-compilation symbol identity, generic
            // constructions, partial-class merges, and metadata-vs-source symbol unification.
            // A hand-rolled walk that compares with SymbolEqualityComparer.Default misses
            // references in downstream projects when the consuming compilation observes the
            // same logical symbol as a distinct ISymbol instance.
            var references = SymbolFinder.FindReferencesAsync(target, loaded.Solution)
                .GetAwaiter().GetResult();

            foreach (var referencedSymbol in references)
            {
                foreach (var location in referencedSymbol.Locations)
                {
                    var sourceTree = location.Location.SourceTree;
                    if (sourceTree == null)
                        continue;

                    var lineSpan = location.Location.GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;

                    if (!seen.Add((file, line)))
                        continue;

                    var node = sourceTree.GetRoot().FindNode(location.Location.SourceSpan);
                    var kind = ClassifyReferenceNode(node);
                    var snippet = GetContainingStatement(node);
                    var projectName = resolver.GetProjectName(location.Document.Project.Id);

                    results.Add(new SymbolReference(
                        kind, file, line, snippet, projectName, resolver.IsGenerated(file)));
                }
            }
        }

        return results;
    }

    private static string ClassifyReferenceNode(SyntaxNode node)
    {
        var identifier = node as IdentifierNameSyntax
            ?? node.FirstAncestorOrSelf<IdentifierNameSyntax>();
        if (identifier != null)
            return ClassifyReference(identifier);

        if (node is GenericNameSyntax || node.FirstAncestorOrSelf<GenericNameSyntax>() != null)
            return "type_argument";

        return "usage";
    }

    private static string ClassifyReference(IdentifierNameSyntax identifier)
    {
        var parent = identifier.Parent;
        return parent switch
        {
            AssignmentExpressionSyntax assignment when assignment.Left == identifier => "assignment",
            ArgumentSyntax => "argument",
            TypeConstraintSyntax => "type_constraint",
            BaseTypeSyntax => "base_type",
            ObjectCreationExpressionSyntax => "instantiation",
            TypeArgumentListSyntax => "type_argument",
            _ => "usage"
        };
    }

    private static string GetContainingStatement(SyntaxNode node)
    {
        var statement = node.FirstAncestorOrSelf<StatementSyntax>();
        var text = (statement ?? node.Parent ?? node).ToString();
        return text.Length > 200 ? text[..200] + "..." : text;
    }
}
