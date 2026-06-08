using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindCallersLogic
{
    public static IReadOnlyList<CallerInfo> Execute(
        LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
    {
        var targetMethods = source.FindMethods(symbol);
        if (targetMethods.Count == 0)
        {
            var resolved = metadata.Resolve(symbol);
            if (resolved?.Symbol is IMethodSymbol m)
            {
                // Include all overloads with the same name from the same containing type
                // so that generic overloads (e.g. AddScoped<T1,T2>) are all matched.
                var allOverloads = (IReadOnlyList<IMethodSymbol>)(m.ContainingType?.GetMembers(m.Name)
                    .OfType<IMethodSymbol>()
                    .ToArray() ?? []);
                targetMethods = allOverloads.Count > 0 ? allOverloads : [m];
            }
            else
                return [];
        }

        var targetSet = new HashSet<IMethodSymbol>(targetMethods, SymbolSignatureComparer.Instance);
        // targetMetadataKeys retains the name-based string fallback for metadata-only symbols
        // whose containing type may not be in the loaded solution's source at all.
        var targetMetadataKeys = BuildMetadataKeys(targetMethods);
        var results = new List<CallerInfo>();
        var seen = new HashSet<(string, int)>();

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

                    if (!IsMethodMatch(calledMethod, targetSet, targetMethods, targetMetadataKeys))
                        continue;

                    var lineSpan = invocation.GetLocation().GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;

                    if (!seen.Add((file, line)))
                        continue;

                    var callerName = GetCallerName(invocation);
                    var snippet = invocation.ToString();

                    results.Add(new CallerInfo(callerName, file, line, snippet, projectName, source.IsGenerated(file)));
                }
            }
        }

        return results;
    }

    private static HashSet<string> BuildMetadataKeys(IReadOnlyList<IMethodSymbol> methods)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in methods)
        {
            if (m.Locations.All(l => !l.IsInSource))
            {
                var typeName = m.ContainingType?.ToDisplayString() ?? string.Empty;
                keys.Add($"{typeName}.{m.Name}");
            }
        }
        return keys;
    }

    private static bool IsMethodMatch(
        IMethodSymbol calledMethod,
        HashSet<IMethodSymbol> targetSet,
        IReadOnlyList<IMethodSymbol> targetMethods,
        HashSet<string> targetMetadataKeys)
    {
        if (targetSet.Contains(calledMethod) || targetSet.Contains(calledMethod.OriginalDefinition))
            return true;

        // Cross-compilation fallback for metadata symbols: compare by containing type name + method name
        if (targetMetadataKeys.Count > 0 && calledMethod.Locations.All(l => !l.IsInSource))
        {
            var typeName = (calledMethod.OriginalDefinition.ContainingType ?? calledMethod.ContainingType)
                ?.ToDisplayString() ?? string.Empty;
            if (targetMetadataKeys.Contains($"{typeName}.{calledMethod.Name}"))
                return true;
        }

        for (int i = 0; i < targetMethods.Count; i++)
        {
            if (!string.Equals(calledMethod.Name, targetMethods[i].Name, StringComparison.Ordinal))
                continue;
            if (IsInterfaceImplementation(calledMethod, targetMethods[i]))
                return true;
        }

        return false;
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
                SymbolSignatureComparer.Instance.Equals(implementation, calledMethod))
                return true;
        }

        // If the called method is an interface method, check if it matches the target
        if (calledMethod.ContainingType.TypeKind == TypeKind.Interface &&
            targetMethod.ContainingType.TypeKind == TypeKind.Interface)
        {
            return SymbolSignatureComparer.Instance.Equals(
                calledMethod.ContainingType, targetMethod.ContainingType) &&
                string.Equals(calledMethod.Name, targetMethod.Name, StringComparison.Ordinal);
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
