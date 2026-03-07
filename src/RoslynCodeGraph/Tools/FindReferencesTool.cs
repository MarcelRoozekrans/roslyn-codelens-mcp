using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindReferencesLogic
{
    public static List<SymbolReference> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var targets = resolver.FindSymbols(symbol);
        if (targets.Count == 0)
            return [];

        var targetSet = new HashSet<ISymbol>(targets, SymbolEqualityComparer.Default);
        // Also add original definitions for generic/overridden symbols
        foreach (var t in targets)
        {
            if (t.OriginalDefinition != null)
                targetSet.Add(t.OriginalDefinition);
        }

        var results = new List<SymbolReference>();
        var seen = new HashSet<(string, int)>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var node in root.DescendantNodes())
                {
                    ISymbol? referencedSymbol = null;
                    string kind = "usage";

                    switch (node)
                    {
                        case IdentifierNameSyntax identifier:
                            var symbolInfo = semanticModel.GetSymbolInfo(identifier);
                            referencedSymbol = symbolInfo.Symbol;
                            kind = ClassifyReference(identifier);
                            break;

                        case GenericNameSyntax genericName:
                            var genericInfo = semanticModel.GetSymbolInfo(genericName);
                            referencedSymbol = genericInfo.Symbol;
                            kind = "type_argument";
                            break;
                    }

                    if (referencedSymbol == null)
                        continue;

                    if (!IsMatch(referencedSymbol, targetSet))
                        continue;

                    var lineSpan = node.GetLocation().GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;

                    if (!seen.Add((file, line)))
                        continue;

                    var snippet = GetContainingStatement(node);
                    results.Add(new SymbolReference(kind, file, line, snippet, projectName, resolver.IsGenerated(file)));
                }
            }
        }

        return results;
    }

    private static bool IsMatch(ISymbol candidate, HashSet<ISymbol> targets)
    {
        if (targets.Contains(candidate))
            return true;
        if (candidate.OriginalDefinition != null && targets.Contains(candidate.OriginalDefinition))
            return true;
        // For interface implementations
        if (candidate is IMethodSymbol method)
        {
            foreach (var target in targets)
            {
                if (target is IMethodSymbol targetMethod &&
                    targetMethod.ContainingType?.TypeKind == TypeKind.Interface)
                {
                    var impl = method.ContainingType?.FindImplementationForInterfaceMember(targetMethod);
                    if (impl != null && SymbolEqualityComparer.Default.Equals(impl, method))
                        return true;
                }
            }
        }
        return false;
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

[McpServerToolType]
public static class FindReferencesTool
{
    [McpServerTool(Name = "find_references"),
     Description("Find all references to a symbol (type, method, property, field, or event) across the solution")]
    public static List<SymbolReference> Execute(
        SolutionManager manager,
        [Description("Symbol name: simple type (MyClass), fully qualified (Namespace.MyClass), or member (MyClass.MyProperty)")] string symbol)
    {
        manager.EnsureLoaded();
        return FindReferencesLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
    }
}
