using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Analysis;
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

        var targetKeys = BuildTargetKeys(targetEvents);
        var results = new List<EventSubscriberInfo>();
        var seen = new HashSet<(string, TextSpan)>();

        // Which trees to walk — once each, project attribution deterministic — is
        // SolutionScanner's job; this loop only asks what each node is. includeGenerated is set
        // BECAUSE this tool reports generated subscriptions on purpose and flags them below: a
        // generated `+=` with no matching `-=` leaks exactly like a hand-written one, so the caller
        // decides. The scanner's default would drop them, and dropping them silently is the whole
        // risk here.
        foreach (var scan in SolutionScanner.EnumerateTrees(loaded, source, includeGenerated: true))
        {
            var projectName = scan.ProjectName;
            var syntaxTree = scan.Tree;

            // The model must come from the tree's OWN compilation, which is what the scan hands
            // back; binding a tree against any other would resolve nothing.
            var semanticModel = scan.SemanticModel();

            foreach (var asg in scan.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
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
                if (!IsEventMatch(called, targetKeys, targetEvents))
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

        results.Sort((a, b) =>
        {
            var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
            return fileCmp != 0 ? fileCmp : a.Line.CompareTo(b.Line);
        });

        return results;
    }

    /// <summary>
    /// The fully-qualified names the target events answer to — the declaration itself and, for a
    /// constructed generic, its original definition.
    /// </summary>
    /// <remarks>
    /// Names rather than symbols because the two sides of the match live in different compilations:
    /// the targets come from <see cref="SymbolResolver"/>/<see cref="MetadataSymbolResolver"/>,
    /// which keep whichever compilation reached the type first — an arbitrary, run-varying choice —
    /// while each <c>+=</c> is bound under the one compilation the scanner picked for its tree.
    /// <see cref="SymbolEqualityComparer"/> across that boundary is always false, so every
    /// subscription would disappear whenever the two disagreed. See <see cref="SymbolKeys"/>.
    /// <para>
    /// This subsumes the separate metadata-only key set this method replaced: a metadata event's
    /// key was its containing type's display name plus its own name, which is exactly its FQN.
    /// </para>
    /// </remarks>
    private static HashSet<string> BuildTargetKeys(IReadOnlyList<IEventSymbol> events)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            keys.Add(SymbolKeys.Fqn(e));
            keys.Add(SymbolKeys.Fqn(e.OriginalDefinition));
        }
        return keys;
    }

    private static bool IsEventMatch(
        IEventSymbol called,
        HashSet<string> targetKeys,
        IReadOnlyList<IEventSymbol> targetEvents)
    {
        if (targetKeys.Contains(SymbolKeys.Fqn(called)) ||
            targetKeys.Contains(SymbolKeys.Fqn(called.OriginalDefinition)))
        {
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

    /// <summary>
    /// Whether a subscription to <paramref name="called"/> reaches <paramref name="target"/> through
    /// an interface: the interface event itself, or the member implementing it on a type that
    /// declares the interface.
    /// </summary>
    /// <remarks>
    /// Compares containing types by FQN for the reason in <see cref="BuildTargetKeys"/>. That also
    /// replaces <c>FindImplementationForInterfaceMember</c>, which takes a symbol of the SAME
    /// compilation and simply returns null for a target from another — so the check it guarded
    /// never fired across the boundary anyway. Callers only reach here with matching names.
    /// </remarks>
    private static bool IsInterfaceImplementation(IEventSymbol called, IEventSymbol target)
    {
        if (target.ContainingType.TypeKind != TypeKind.Interface) return false;
        if (!string.Equals(called.Name, target.Name, StringComparison.Ordinal)) return false;

        var targetTypeFqn = SymbolKeys.Fqn(target.ContainingType);

        // The interface event itself, seen through any compilation.
        if (string.Equals(SymbolKeys.Fqn(called.ContainingType), targetTypeFqn, StringComparison.Ordinal))
            return true;

        // An event of the same name on a type that implements the interface — the implementation,
        // implicit or an explicit re-declaration.
        foreach (var iface in called.ContainingType.AllInterfaces)
        {
            if (string.Equals(SymbolKeys.Fqn(iface), targetTypeFqn, StringComparison.Ordinal))
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
