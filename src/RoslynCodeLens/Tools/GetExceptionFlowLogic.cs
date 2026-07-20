using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Which exceptions can escape a method: a depth-bounded walk of its callees that propagates
/// every raised exception back up the call chain, testing each call site's try/catch on the way.
/// </summary>
public static class GetExceptionFlowLogic
{
    /// <summary>One frame of the call chain: the method containing the call, and the call itself.</summary>
    private readonly record struct Frame(string Method, SyntaxNode CallSite);

    public static ExceptionFlowResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        MetadataSymbolResolver metadata,
        string method,
        int maxDepth,
        bool includeDocumented,
        int maxNodes,
        CancellationToken cancellationToken = default)
    {
        var root = resolver.FindMethods(method).FirstOrDefault()
            ?? metadata.Resolve(method)?.Symbol as IMethodSymbol;

        if (root is null || !root.Locations.Any(l => l.IsInSource))
        {
            throw new McpToolException(
                ToolErrorCode.SymbolNotFound,
                $"Source method '{method}' not found.",
                new { method });
        }

        var walker = new Walker(loaded, maxDepth, maxNodes, includeDocumented, cancellationToken);
        walker.Visit(root, depth: 0, chain: []);

        var exceptions = walker.Results;
        return new ExceptionFlowResult(
            Method: root.ToDisplayString(),
            MaxDepthRequested: maxDepth,
            Truncated: walker.Truncated,
            Exceptions: exceptions,
            Summary: BuildSummary(exceptions));
    }

    internal static object BuildSummary(IReadOnlyList<ExceptionFlowInfo> exceptions)
    {
        var byType = exceptions
            .GroupBy(e => e.ExceptionType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return new
        {
            escaping = exceptions.Count(e => e.Escapes),
            caught = exceptions.Count(e => !e.Escapes),
            byType,
        };
    }

    private sealed class Walker
    {
        private readonly Dictionary<SyntaxTree, Compilation> _treeToCompilation = new();
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private readonly HashSet<(string Type, string RaisedIn, string File, int Line)> _seen = [];
        private readonly List<ExceptionFlowInfo> _results = [];
        private readonly int _maxDepth;
        private readonly int _maxNodes;
        private readonly bool _includeDocumented;
        // Deliberately not readonly: CancellationToken is a non-readonly struct, so calling
        // ThrowIfCancellationRequested on a readonly field copies it on every call (EPS06).
        private CancellationToken _cancellationToken;
        private int _visitedCount;

        public Walker(
            LoadedSolution loaded, int maxDepth, int maxNodes, bool includeDocumented,
            CancellationToken cancellationToken)
        {
            _maxDepth = maxDepth;
            _maxNodes = maxNodes;
            _includeDocumented = includeDocumented;
            _cancellationToken = cancellationToken;

            // Same idiom as GetCallGraphLogic: a semantic model must come from the compilation
            // that owns the tree, so map every tree back to its compilation once up front.
            foreach (var (_, compilation) in loaded.Compilations)
            {
                foreach (var tree in compilation.SyntaxTrees)
                    _treeToCompilation[tree] = compilation;
            }
        }

        public bool Truncated { get; private set; }

        public IReadOnlyList<ExceptionFlowInfo> Results => _results;

        public void Visit(IMethodSymbol method, int depth, IReadOnlyList<Frame> chain)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (!_visited.Add(method.ToDisplayString()))
                return;

            if (_visitedCount >= _maxNodes)
            {
                Truncated = true;
                return;
            }

            _visitedCount++;

            var declaration = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_cancellationToken);
            if (declaration is null || ModelFor(declaration) is not { } model)
                return;

            var display = method.ToDisplayString();

            foreach (var site in ExceptionAnalyzer.CollectThrowSites(declaration, model))
                Resolve(site.ExceptionType, site.Node, display, chain, origin: "thrown");

            foreach (var (callee, callSite) in EnumerateCallSites(declaration, model))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (!callee.Locations.Any(l => l.IsInSource))
                {
                    if (!_includeDocumented)
                        continue;

                    // A metadata callee has no body to analyse; its XML docs are the only
                    // statement we have about what it raises.
                    foreach (var documented in
                             ExceptionAnalyzer.GetDocumentedExceptions(callee, model.Compilation))
                    {
                        Resolve(documented, callSite, callee.ToDisplayString(), chain, origin: "documented");
                    }

                    continue;
                }

                if (depth + 1 > _maxDepth)
                {
                    Truncated = true;
                    continue;
                }

                Visit(callee, depth + 1, [.. chain, new Frame(display, callSite)]);
            }
        }

        /// <summary>
        /// Decide the fate of one raised exception: handled where it is raised, handled by some
        /// caller's try/catch around the call site that led here, or escaping the root. A handler
        /// with a <c>when</c> filter may decline at runtime, so it never stops the walk — it only
        /// records that a filter is in play and the exception is still reported as escaping.
        /// </summary>
        private void Resolve(
            INamedTypeSymbol exceptionType,
            SyntaxNode raisingNode,
            string raisedIn,
            IReadOnlyList<Frame> chain,
            string origin)
        {
            var position = raisingNode.GetLocation().GetLineSpan();
            var file = position.Path;
            var line = position.StartLinePosition.Line + 1;
            var typeName = ExceptionQueries.Fqn(exceptionType);

            if (!_seen.Add((typeName, raisedIn, file, line)))
                return;

            var hasFilter = false;

            foreach (var node in Outward(raisingNode, chain))
            {
                if (ModelFor(node) is not { } model)
                    continue;

                var handler = ExceptionAnalyzer.FindHandler(
                    node, ExceptionQueries.Localize(exceptionType, model.Compilation), model);

                if (handler is null)
                    continue;

                if (handler.HasFilter)
                {
                    hasFilter = true;
                    continue;
                }

                var caughtAt = handler.Clause.GetLocation().GetLineSpan();
                _results.Add(new ExceptionFlowInfo(
                    ExceptionType: typeName,
                    Origin: origin,
                    RaisedIn: raisedIn,
                    File: file,
                    Line: line,
                    Depth: chain.Count,
                    Path: BuildPath(chain, raisedIn),
                    Escapes: false,
                    CaughtIn: ExceptionQueries.DescribeContainingMember(handler.Clause, model),
                    CaughtFile: caughtAt.Path,
                    CaughtLine: caughtAt.StartLinePosition.Line + 1,
                    HasFilter: hasFilter));
                return;
            }

            _results.Add(new ExceptionFlowInfo(
                ExceptionType: typeName,
                Origin: origin,
                RaisedIn: raisedIn,
                File: file,
                Line: line,
                Depth: chain.Count,
                Path: BuildPath(chain, raisedIn),
                Escapes: true,
                CaughtIn: null,
                CaughtFile: null,
                CaughtLine: null,
                HasFilter: hasFilter));
        }

        /// <summary>
        /// The nodes whose enclosing try/catch statements could stop this exception, innermost
        /// first: the raising node itself, then each call site back down the chain to the root.
        /// </summary>
        private static IEnumerable<SyntaxNode> Outward(SyntaxNode raisingNode, IReadOnlyList<Frame> chain)
        {
            yield return raisingNode;

            for (var i = chain.Count - 1; i >= 0; i--)
                yield return chain[i].CallSite;
        }

        private static IReadOnlyList<string> BuildPath(IReadOnlyList<Frame> chain, string raisedIn)
            => [.. chain.Select(f => f.Method), raisedIn];

        /// <summary>
        /// Outgoing calls paired with the syntax node that makes them. GetCallGraphLogic's
        /// enumeration yields only the callee symbol, which is not enough here: containment of
        /// the CALL SITE in a try block is what decides whether a caller catches. Calls inside
        /// lambdas and local functions are skipped for the same reason throws inside them are —
        /// they run when that body runs, not at this method's boundary.
        /// </summary>
        private static IEnumerable<(IMethodSymbol Callee, SyntaxNode CallSite)> EnumerateCallSites(
            SyntaxNode declaration, SemanticModel model)
        {
            foreach (var node in declaration.DescendantNodes(
                         descendIntoChildren: ExceptionAnalyzer.DescendIntoBody))
            {
                var called = node switch
                {
                    InvocationExpressionSyntax
                        or ObjectCreationExpressionSyntax
                        or ImplicitObjectCreationExpressionSyntax
                        => model.GetSymbolInfo(node).Symbol as IMethodSymbol,
                    _ => null,
                };

                if (called != null)
                    yield return (called, node);
            }
        }

        private SemanticModel? ModelFor(SyntaxNode node)
        {
            var tree = node.SyntaxTree;
            if (_models.TryGetValue(tree, out var cached))
                return cached;

            if (!_treeToCompilation.TryGetValue(tree, out var compilation))
                return null;

            var model = compilation.GetSemanticModel(tree);
            _models[tree] = model;
            return model;
        }
    }
}
