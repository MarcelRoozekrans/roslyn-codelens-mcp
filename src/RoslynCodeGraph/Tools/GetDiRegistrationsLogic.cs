using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetDiRegistrationsLogic
{
    private static readonly HashSet<string> DiMethodNames = new(StringComparer.Ordinal)
    {
        "AddTransient", "AddScoped", "AddSingleton",
        "TryAddTransient", "TryAddScoped", "TryAddSingleton",
        "AddKeyedTransient", "AddKeyedScoped", "AddKeyedSingleton"
    };

    public static IReadOnlyList<DiRegistration> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var results = new List<DiRegistration>();

        foreach (var (_, compilation) in loaded.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var methodName = GetMethodName(invocation);
                    if (methodName == null || !DiMethodNames.Contains(methodName))
                        continue;

                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
                        continue;

                    var typeArgs = methodSymbol.TypeArguments;
                    if (typeArgs.Length == 0)
                        continue;

                    string serviceName;
                    string implementationName;

                    if (typeArgs.Length >= 2)
                    {
                        serviceName = typeArgs[0].ToDisplayString();
                        implementationName = typeArgs[1].ToDisplayString();
                    }
                    else
                    {
                        serviceName = typeArgs[0].ToDisplayString();
                        implementationName = serviceName;
                    }

                    if (!MatchesSymbol(serviceName, symbol) && !MatchesSymbol(implementationName, symbol))
                        continue;

                    var lifetime = ExtractLifetime(methodName);
                    var lineSpan = tree.GetLineSpan(invocation.Span);
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;

                    results.Add(new DiRegistration(serviceName, implementationName, lifetime, file, line));
                }
            }
        }

        return results;
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    private static bool MatchesSymbol(string fullName, string symbol)
    {
        if (symbol.Contains('.', StringComparison.Ordinal))
            return string.Equals(fullName, symbol, StringComparison.Ordinal);

        var lastDot = fullName.LastIndexOf('.');
        if (lastDot >= 0)
            return string.Compare(fullName, lastDot + 1, symbol, 0, symbol.Length, StringComparison.Ordinal) == 0
                   && fullName.Length - lastDot - 1 == symbol.Length;
        return string.Equals(fullName, symbol, StringComparison.Ordinal);
    }

    private static string ExtractLifetime(string methodName)
    {
        var name = methodName;
        if (name.StartsWith("TryAdd", StringComparison.Ordinal))
            name = name.Substring(6);
        else if (name.StartsWith("Add", StringComparison.Ordinal))
            name = name.Substring(3);

        if (name.StartsWith("Keyed", StringComparison.Ordinal))
            name = name.Substring(5);

        return name;
    }
}
