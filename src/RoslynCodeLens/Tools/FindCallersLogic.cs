using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
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

        var results = new List<CallerInfo>();
        var seen = new HashSet<(string, int)>();

        foreach (var target in targetMethods)
        {
            // Roslyn's SymbolFinder.FindCallersAsync handles cross-compilation symbol
            // identity, virtual-dispatch cascading, and interface→implementation matching.
            var callerInfos = SymbolFinder.FindCallersAsync(target, loaded.Solution)
                .GetAwaiter().GetResult();

            foreach (var callerInfo in callerInfos)
            {
                foreach (var location in callerInfo.Locations)
                {
                    var sourceTree = location.SourceTree;
                    if (sourceTree == null)
                        continue;

                    var lineSpan = location.GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;

                    if (!seen.Add((file, line)))
                        continue;

                    var node = sourceTree.GetRoot().FindNode(location.SourceSpan);
                    var invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                    var snippet = (invocation ?? node).ToString();
                    var callerName = GetCallerName(callerInfo.CallingSymbol, invocation ?? node);

                    var document = loaded.Solution.GetDocument(sourceTree);
                    var projectName = document != null
                        ? source.GetProjectName(document.Project.Id)
                        : string.Empty;

                    results.Add(new CallerInfo(
                        callerName, file, line, snippet, projectName, source.IsGenerated(file)));
                }
            }
        }

        return results;
    }

    private static string GetCallerName(ISymbol? callingSymbol, SyntaxNode node)
    {
        if (callingSymbol is IMethodSymbol caller && caller.ContainingType != null)
            return $"{caller.ContainingType.Name}.{caller.Name}";

        var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

        if (type != null && method != null)
            return $"{type.Identifier.Text}.{method.Identifier.Text}";
        if (type != null)
            return type.Identifier.Text;

        return "<unknown>";
    }
}
