using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using RoslynCodeLens.Analysis;
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
        var seen = new HashSet<(string File, int Line, int Column)>();
        // Semantic models are expensive, and only the rare COM-interop argument needs one.
        // Built on demand (see GetModel) and reused per document for the rest of the scan.
        var models = new Dictionary<DocumentId, SemanticModel>();

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
                    var column = lineSpan.StartLinePosition.Character + 1;

                    if (!seen.Add((file, line, column)))
                        continue;

                    // getInnermostNodeForTie: a base-type entry such as `class Bar : Foo` has the
                    // same span as its type name, and the default outermost pick would hand the
                    // classifier a node with no SimpleName to read.
                    var node = sourceTree.GetRoot()
                        .FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);

                    // Resolved lazily: only the rare argument that omits an explicit `out`/`ref`
                    // (COM interop) needs a model, so a solution-wide scan no longer builds and
                    // pins one for every document it touches.
                    var document = location.Document;
                    var kind = ReferenceClassifier.Classify(
                        node, referencedSymbol.Definition, () => GetModel(models, document));
                    var snippet = ExceptionQueries.StatementSnippet(node);
                    var projectName = resolver.GetProjectName(location.Document.Project.Id);

                    results.Add(new SymbolReference(
                        kind, file, line, column, snippet, projectName, resolver.IsGenerated(file),
                        resolver.GetMarkupSource(file)));
                }
            }
        }

        return results;
    }

    private static SemanticModel? GetModel(Dictionary<DocumentId, SemanticModel> cache, Document document)
    {
        if (cache.TryGetValue(document.Id, out var cached))
            return cached;

        var model = document.GetSemanticModelAsync().GetAwaiter().GetResult();
        if (model != null)
            cache[document.Id] = model;
        return model;
    }
}
