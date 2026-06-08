using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

        var targetSet = BuildTargetSet(targets);
        return ScanForReferences(loaded, source, targetSet);
    }

    private static HashSet<ISymbol> BuildTargetSet(IReadOnlyList<ISymbol> targets)
    {
        var targetSet = new HashSet<ISymbol>(targets, SymbolSignatureComparer.Instance);
        foreach (var t in targets)
        {
            if (t.OriginalDefinition != null)
                targetSet.Add(t.OriginalDefinition);
        }
        return targetSet;
    }

    private static List<SymbolReference> ScanForReferences(
        LoadedSolution loaded, SymbolResolver resolver, HashSet<ISymbol> targetSet)
    {
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
                    var (referencedSymbol, kind) = ResolveNodeSymbol(node, semanticModel);

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

    private static (ISymbol? Symbol, string Kind) ResolveNodeSymbol(SyntaxNode node, SemanticModel semanticModel)
    {
        return node switch
        {
            IdentifierNameSyntax identifier => (semanticModel.GetSymbolInfo(identifier).Symbol, ClassifyReference(identifier)),
            GenericNameSyntax genericName => (semanticModel.GetSymbolInfo(genericName).Symbol, "type_argument"),
            _ => (null, "usage")
        };
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
                    if (impl != null && SymbolSignatureComparer.Instance.Equals(impl, method))
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
