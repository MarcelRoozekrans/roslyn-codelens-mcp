using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class FindEventSubscribersLogic
{
    public static IReadOnlyList<EventSubscriberInfo> Execute(
        LoadedSolution loaded, SymbolResolver source, MetadataSymbolResolver metadata, string symbol)
    {
        var targetEvents = source.FindEvents(symbol);
        if (targetEvents.Count == 0)
        {
            var resolved = metadata.Resolve(symbol);
            if (resolved?.Symbol is IEventSymbol e)
                targetEvents = [e];
            else
                return [];
        }

        var targetSet = new HashSet<IEventSymbol>(targetEvents, SymbolEqualityComparer.Default);
        var targetMetadataKeys = BuildMetadataKeys(targetEvents);
        var results = new List<EventSubscriberInfo>();
        var seen = new HashSet<(string, TextSpan)>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = source.GetProjectName(projectId);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var asg in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    var kind = asg.Kind() switch
                    {
                        SyntaxKind.AddAssignmentExpression => SubscriptionKind.Subscribe,
                        SyntaxKind.SubtractAssignmentExpression => SubscriptionKind.Unsubscribe,
                        _ => (SubscriptionKind?)null
                    };
                    if (kind is null) continue;

                    if (semanticModel.GetSymbolInfo(asg.Left).Symbol is not IEventSymbol called)
                        continue;
                    if (!IsEventMatch(called, targetSet, targetEvents, targetMetadataKeys))
                        continue;

                    if (!seen.Add((syntaxTree.FilePath, asg.Span)))
                        continue;

                    var lineSpan = asg.GetLocation().GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var handler = ResolveHandlerName(asg.Right, semanticModel, file, line);

                    var sourceText = syntaxTree.GetText();
                    var lineText = sourceText.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

                    results.Add(new EventSubscriberInfo(
                        EventName: called.ToDisplayString(),
                        HandlerName: handler,
                        Kind: kind.Value,
                        FilePath: file,
                        Line: line,
                        Snippet: lineText,
                        Project: projectName,
                        IsGenerated: source.IsGenerated(file)));
                }
            }
        }

        results.Sort((a, b) =>
        {
            var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
            return fileCmp != 0 ? fileCmp : a.Line.CompareTo(b.Line);
        });

        return results;
    }

    private static HashSet<string> BuildMetadataKeys(IReadOnlyList<IEventSymbol> events)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            if (e.Locations.All(l => !l.IsInSource))
            {
                var typeName = e.ContainingType?.ToDisplayString() ?? string.Empty;
                keys.Add($"{typeName}.{e.Name}");
            }
        }
        return keys;
    }

    private static bool IsEventMatch(
        IEventSymbol called,
        HashSet<IEventSymbol> targetSet,
        IReadOnlyList<IEventSymbol> targetEvents,
        HashSet<string> targetMetadataKeys)
    {
        if (targetSet.Contains(called) || targetSet.Contains((IEventSymbol)called.OriginalDefinition))
            return true;

        if (targetMetadataKeys.Count > 0 && called.Locations.All(l => !l.IsInSource))
        {
            var typeName = (called.OriginalDefinition.ContainingType ?? called.ContainingType)
                ?.ToDisplayString() ?? string.Empty;
            if (targetMetadataKeys.Contains($"{typeName}.{called.Name}"))
                return true;
        }

        for (int i = 0; i < targetEvents.Count; i++)
        {
            if (!string.Equals(called.Name, targetEvents[i].Name, StringComparison.Ordinal))
                continue;
            if (IsInterfaceImplementation(called, targetEvents[i]))
                return true;
        }

        return false;
    }

    private static bool IsInterfaceImplementation(IEventSymbol called, IEventSymbol target)
    {
        if (target.ContainingType.TypeKind == TypeKind.Interface)
        {
            if (SymbolEqualityComparer.Default.Equals(called, target))
                return true;

            var containingType = called.ContainingType;
            var implementation = containingType.FindImplementationForInterfaceMember(target);
            if (implementation != null &&
                SymbolEqualityComparer.Default.Equals(implementation, called))
                return true;
        }

        if (called.ContainingType.TypeKind == TypeKind.Interface &&
            target.ContainingType.TypeKind == TypeKind.Interface)
        {
            return SymbolEqualityComparer.Default.Equals(
                       called.ContainingType, target.ContainingType) &&
                   string.Equals(called.Name, target.Name, StringComparison.Ordinal);
        }

        // Last-resort: target is interface event, called is a non-interface event whose containing type
        // implements the interface (covers explicit re-declarations of interface events).
        if (target.ContainingType.TypeKind == TypeKind.Interface &&
            called.ContainingType.TypeKind != TypeKind.Interface &&
            string.Equals(called.Name, target.Name, StringComparison.Ordinal) &&
            called.ContainingType.AllInterfaces.Contains(target.ContainingType, SymbolEqualityComparer.Default))
        {
            return true;
        }

        return false;
    }

    private static string ResolveHandlerName(
        ExpressionSyntax rhs, SemanticModel semanticModel, string file, int line)
    {
        switch (rhs)
        {
            case LambdaExpressionSyntax:
                return $"<lambda at {file}:{line}>";

            case AnonymousMethodExpressionSyntax:
                return $"<anonymous-method at {file}:{line}>";

            case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                if (semanticModel.GetSymbolInfo(rhs).Symbol is IMethodSymbol m)
                    return m.ToDisplayString();
                break;
        }

        return $"<expression at {file}:{line}>";
    }
}
