using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindTestsForSymbolLogic
{
    public static FindTestsForSymbolResult Execute(
        LoadedSolution loaded,
        SymbolResolver source,
        string symbol,
        bool transitive = false,
        int maxDepth = 3)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 5);

        var targetMethods = source.FindMethods(symbol);
        if (targetMethods.Count == 0)
            return new FindTestsForSymbolResult(symbol, [], []);

        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);
        if (testProjectIds.IsEmpty)
            return new FindTestsForSymbolResult(symbol, [], []);

        var directTests = new List<TestReference>();
        var transitiveTests = new List<TestReference>();
        BfsWalk(loaded, source, targetMethods, transitive, maxDepth, directTests, transitiveTests);

        return new FindTestsForSymbolResult(symbol, directTests, transitiveTests);
    }

    private static void BfsWalk(
        LoadedSolution loaded,
        SymbolResolver source,
        IReadOnlyList<IMethodSymbol> targetMethods,
        bool transitive,
        int maxDepth,
        List<TestReference> directTests,
        List<TestReference> transitiveTests)
    {
        // Report each test once, even when it exists as one instance per target framework...
        var seenTestSymbols = new HashSet<IMethodSymbol>(source.SymbolComparer);
        // ...but classify every instance: a test attribute may exist on one target framework only.
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        // BFS: each entry is (method we want callers for, chain so far ending with the target method's short name)
        var queue = new Queue<(IMethodSymbol Method, List<string> Chain, int Depth)>();
        foreach (var t in targetMethods)
        {
            queue.Enqueue((t, [t.Name], 0));
            visited.Add(t);
        }

        while (queue.Count > 0)
        {
            var (frontier, chain, depth) = queue.Dequeue();

            foreach (var caller in EnumerateDirectCallers(loaded, source, frontier))
            {
                if (!visited.Add(caller.Method))
                    continue;

                var classification = ClassifyAsTest(caller.Method, caller.ProjectName);
                if (classification is not null)
                {
                    if (!seenTestSymbols.Add(caller.Method))
                        continue;

                    if (depth == 0)
                    {
                        // Direct hit
                        directTests.Add(classification);
                    }
                    else if (transitive)
                    {
                        // Transitive hit — attach call chain (caller's path through helpers to target)
                        transitiveTests.Add(classification with { CallChain = chain });
                    }
                    // Tests are terminal — never expand past them.
                    continue;
                }

                // Non-test caller. In transitive mode, enqueue if depth budget remains.
                if (transitive && depth + 1 < maxDepth)
                {
                    var newChain = new List<string>(chain.Count + 1) { caller.Method.Name };
                    newChain.AddRange(chain);
                    queue.Enqueue((caller.Method, newChain, depth + 1));
                }
            }
        }
    }

    private record DirectCaller(IMethodSymbol Method, string ProjectName);

    private static IEnumerable<DirectCaller> EnumerateDirectCallers(
        LoadedSolution loaded,
        SymbolResolver source,
        IMethodSymbol target)
    {
        // Two-pass scan: test projects (terminal lookups) and non-test projects
        // (helper hops in transitive mode). We need both because a helper might live
        // in TestLib2 (non-test project) and a test in NUnitFixture invokes it.
        var comparer = source.SymbolComparer;
        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = source.GetProjectName(projectId);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol calledMethod)
                        continue;

                    if (!comparer.Equals(calledMethod, target) &&
                        !comparer.Equals(calledMethod.OriginalDefinition, target) &&
                        !comparer.Equals(calledMethod, target.OriginalDefinition))
                        continue;

                    var enclosing = FindEnclosingMethodSymbol(invocation, semanticModel);
                    if (enclosing is null)
                        continue;

                    yield return new DirectCaller(enclosing, projectName);
                }
            }
        }
    }

    private static IMethodSymbol? FindEnclosingMethodSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        var methodDecl = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDecl is null)
            return null;
        return semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
    }

    private static TestReference? ClassifyAsTest(IMethodSymbol method, string projectName)
    {
        var classification = TestMethodClassifier.Classify(method);
        if (classification is null) return null;

        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null) return null;

        var lineSpan = location.GetLineSpan();

        return new TestReference(
            FullyQualifiedName: method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal),
            Framework: classification.Framework,
            Attribute: classification.AttributeShortName,
            FilePath: lineSpan.Path,
            Line: lineSpan.StartLinePosition.Line + 1,
            Project: projectName);
    }
}
