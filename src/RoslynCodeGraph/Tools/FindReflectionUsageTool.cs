using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindReflectionUsageLogic
{
    private static readonly Dictionary<string, string> ReflectionMethodsMap = new()
    {
        ["GetType"] = "dynamic_instantiation",
        ["CreateInstance"] = "dynamic_instantiation",
        ["Invoke"] = "method_invoke",
        ["GetMethod"] = "method_invoke",
        ["GetTypes"] = "assembly_scan",
        ["GetExportedTypes"] = "assembly_scan",
        ["GetCustomAttributes"] = "attribute_discovery",
        ["GetCustomAttribute"] = "attribute_discovery"
    };

    private static readonly HashSet<string> ReflectionNamespaces = new()
    {
        "System.Type",
        "System.Activator"
    };

    public static List<ReflectionUsage> Execute(LoadedSolution loaded, SymbolResolver resolver, string? symbol)
    {
        var results = new List<ReflectionUsage>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                        continue;

                    var methodName = memberAccess.Name.Identifier.Text;
                    if (!ReflectionMethodsMap.TryGetValue(methodName, out var kind))
                        continue;

                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    var methodSymbol = symbolInfo.Symbol as IMethodSymbol;
                    if (methodSymbol == null)
                        continue;

                    var containingType = methodSymbol.ContainingType?.ToDisplayString();
                    if (containingType == null)
                        continue;

                    if (!IsReflectionType(containingType))
                        continue;

                    var target = ExtractTarget(invocation);
                    var lineSpan = tree.GetLineSpan(invocation.Span);
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var snippet = invocation.ToString();

                    if (symbol != null && !snippet.Contains(symbol) && !target.Contains(symbol))
                        continue;

                    results.Add(new ReflectionUsage(kind, target, file, line, snippet));
                }
            }
        }

        return results;
    }

    private static bool IsReflectionType(string containingType)
    {
        if (ReflectionNamespaces.Contains(containingType))
            return true;

        if (containingType.StartsWith("System.Reflection."))
            return true;

        return false;
    }

    private static string ExtractTarget(InvocationExpressionSyntax invocation)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count > 0)
        {
            var firstArg = args[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal)
                return literal.Token.ValueText;

            return firstArg.ToString();
        }

        return "";
    }
}

[McpServerToolType]
public static class FindReflectionUsageTool
{
    [McpServerTool(Name = "find_reflection_usage"),
     Description("Detect dynamic/reflection-based usage like Type.GetType, Activator.CreateInstance, MethodInfo.Invoke")]
    public static List<ReflectionUsage> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional type name to filter results (omit to scan entire solution)")] string? symbol = null)
    {
        return FindReflectionUsageLogic.Execute(loaded, resolver, symbol);
    }
}
