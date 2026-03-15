using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class AnalyzeMethodLogic
{
    public static MethodAnalysis? Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var methods = resolver.FindMethods(symbol);
        if (methods.Count == 0)
            return null;

        var target = methods[0];
        var (file, line) = resolver.GetFileAndLine(target);
        var projectName = resolver.GetProjectName(target);
        var signature = target.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        var callers = FindCallersLogic.Execute(loaded, resolver, symbol);
        var outgoing = FindOutgoingCalls(loaded, target);

        return new MethodAnalysis(symbol, file, line, projectName, signature, callers, outgoing);
    }

    private static IReadOnlyList<string> FindOutgoingCalls(LoadedSolution loaded, IMethodSymbol target)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);

        // Find the syntax tree containing this method
        var location = target.Locations.FirstOrDefault(l => l.IsInSource);
        if (location == null) return [];

        var syntaxTree = location.SourceTree;
        if (syntaxTree == null) return [];

        // Find the compilation for this tree
        Compilation? compilation = null;
        foreach (var (_, comp) in loaded.Compilations)
        {
            if (comp.SyntaxTrees.Contains(syntaxTree))
            {
                compilation = comp;
                break;
            }
        }
        if (compilation == null) return [];

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();

        // Find the method declaration node
        var methodNode = root.FindNode(location.SourceSpan)
            .FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (methodNode == null) return [];

        // Walk all invocations inside the method
        foreach (var invocation in methodNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol callee)
            {
                var name = callee.ContainingType != null
                    ? $"{callee.ContainingType.Name}.{callee.Name}"
                    : callee.Name;
                results.Add(name);
            }
        }

        return [.. results];
    }
}
