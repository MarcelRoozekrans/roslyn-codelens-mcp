using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetDiRegistrationsLogic
{
    private static readonly HashSet<string> DiMethodNames = new()
    {
        "AddTransient", "AddScoped", "AddSingleton",
        "TryAddTransient", "TryAddScoped", "TryAddSingleton",
        "AddKeyedTransient", "AddKeyedScoped", "AddKeyedSingleton"
    };

    public static List<DiRegistration> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var results = new List<DiRegistration>();

        foreach (var (projectId, compilation) in loaded.Compilations)
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
        if (symbol.Contains('.'))
            return fullName == symbol;

        var lastDot = fullName.LastIndexOf('.');
        var simpleName = lastDot >= 0 ? fullName.AsSpan(lastDot + 1) : fullName.AsSpan();
        return simpleName.SequenceEqual(symbol.AsSpan());
    }

    private static string ExtractLifetime(string methodName)
    {
        var name = methodName;
        if (name.StartsWith("TryAdd"))
            name = name.Substring(6);
        else if (name.StartsWith("Add"))
            name = name.Substring(3);

        if (name.StartsWith("Keyed"))
            name = name.Substring(5);

        return name;
    }
}

[McpServerToolType]
public static class GetDiRegistrationsTool
{
    [McpServerTool(Name = "get_di_registrations"),
     Description("Scan IServiceCollection extension methods for DI registrations of a type")]
    public static List<DiRegistration> Execute(
        SolutionManager manager,
        [Description("Type name to search for (simple or fully qualified)")] string symbol)
    {
        manager.EnsureLoaded();
        return GetDiRegistrationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
