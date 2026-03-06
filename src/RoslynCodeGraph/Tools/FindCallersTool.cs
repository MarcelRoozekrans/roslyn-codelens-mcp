using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindCallersLogic
{
    public static List<CallerInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var targetMethods = resolver.FindMethods(symbol);
        if (targetMethods.Count == 0)
            return [];

        var results = new List<CallerInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    var calledMethod = symbolInfo.Symbol as IMethodSymbol;

                    if (calledMethod == null)
                        continue;

                    // Check if the called method matches any target, including interface implementations
                    bool isMatch = targetMethods.Any(target =>
                        SymbolEqualityComparer.Default.Equals(calledMethod, target) ||
                        SymbolEqualityComparer.Default.Equals(calledMethod.OriginalDefinition, target) ||
                        IsInterfaceImplementation(calledMethod, target));

                    if (!isMatch)
                        continue;

                    var callerName = GetCallerName(invocation);
                    var lineSpan = invocation.GetLocation().GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var snippet = invocation.ToString();

                    var projectName = loaded.Solution.Projects
                        .FirstOrDefault(p => p.Id == projectId)?.Name ?? "";

                    results.Add(new CallerInfo(callerName, file, line, snippet, projectName));
                }
            }
        }

        return results.DistinctBy(r => $"{r.File}:{r.Line}").ToList();
    }

    private static bool IsInterfaceImplementation(IMethodSymbol calledMethod, IMethodSymbol targetMethod)
    {
        // If the target is an interface method, check if the called method's containing type
        // implements that interface and the call resolves through the interface
        if (targetMethod.ContainingType.TypeKind == TypeKind.Interface)
        {
            // Direct interface call: calledMethod is the interface method itself
            if (SymbolEqualityComparer.Default.Equals(calledMethod, targetMethod))
                return true;

            // Check if calledMethod implements the target interface method
            var containingType = calledMethod.ContainingType;
            var implementation = containingType.FindImplementationForInterfaceMember(targetMethod);
            if (implementation != null &&
                SymbolEqualityComparer.Default.Equals(implementation, calledMethod))
                return true;
        }

        // If the called method is an interface method, check if it matches the target
        if (calledMethod.ContainingType.TypeKind == TypeKind.Interface)
        {
            if (targetMethod.ContainingType.TypeKind == TypeKind.Interface)
            {
                return SymbolEqualityComparer.Default.Equals(
                    calledMethod.ContainingType, targetMethod.ContainingType) &&
                    calledMethod.Name == targetMethod.Name;
            }
        }

        return false;
    }

    private static string GetCallerName(SyntaxNode node)
    {
        var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

        if (type != null && method != null)
            return $"{type.Identifier.Text}.{method.Identifier.Text}";
        if (type != null)
            return type.Identifier.Text;

        return "<unknown>";
    }
}

[McpServerToolType]
public static class FindCallersTool
{
    [McpServerTool(Name = "find_callers"),
     Description("Find every call site for a method, property, or constructor")]
    public static List<CallerInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Method name as Type.Method (simple or fully qualified)")] string symbol)
    {
        return FindCallersLogic.Execute(loaded, resolver, symbol);
    }
}
