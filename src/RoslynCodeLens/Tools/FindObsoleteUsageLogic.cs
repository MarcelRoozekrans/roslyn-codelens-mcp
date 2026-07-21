using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens.Models;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindObsoleteUsageLogic
{
    public static FindObsoleteUsageResult Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        bool errorOnly)
    {
        var testProjectIds = TestProjectDetector.GetTestProjectIds(loaded.Solution);

        // Step 1: collect all [Obsolete]-marked symbols across the entire solution (including
        // test projects and metadata symbols). Test-project filtering happens in Step 2 on the
        // *usage site* side: an obsolete symbol declared in a test project is fine — we just
        // won't search test-project syntax trees for usages, so it'll get omitted by the
        // zero-usage filter in Step 3 unless production code calls it.
        // SymbolResolver.AttributeIndex is keyed by simple attribute name; entries are
        // duplicated under both "Obsolete" and "ObsoleteAttribute" — dedup by symbol identity.
        var obsoleteSymbols = new Dictionary<ISymbol, ObsoleteAttributeData>(SymbolEqualityComparer.Default);
        foreach (var key in new[] { "Obsolete", "ObsoleteAttribute" })
        {
            if (!resolver.AttributeIndex.TryGetValue(key, out var entries)) continue;

            foreach (var (symbol, attr) in entries)
            {
                if (attr.AttributeClass?.ToDisplayString() != "System.ObsoleteAttribute") continue;
                if (obsoleteSymbols.ContainsKey(symbol)) continue;

                obsoleteSymbols[symbol] = ParseObsoleteAttribute(attr);
            }
        }

        if (obsoleteSymbols.Count == 0)
            return new FindObsoleteUsageResult([]);

        // Step 2: walk all production syntax trees and collect usages per obsolete symbol.
        var targetSet = new HashSet<ISymbol>(obsoleteSymbols.Keys, SymbolEqualityComparer.Default);
        var usagesByTarget = new Dictionary<ISymbol, List<ObsoleteUsageSite>>(SymbolEqualityComparer.Default);
        foreach (var s in obsoleteSymbols.Keys)
            usagesByTarget[s] = new List<ObsoleteUsageSite>();

        var seen = new HashSet<(string, TextSpan)>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            if (testProjectIds.Contains(projectId)) continue;

            var projectName = resolver.GetProjectName(projectId);
            if (project is not null && !string.Equals(projectName, project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var node in root.DescendantNodes())
                {
                    if (node is not (InvocationExpressionSyntax
                                     or ObjectCreationExpressionSyntax
                                     or MemberAccessExpressionSyntax
                                     or IdentifierNameSyntax))
                        continue;

                    // Skip [Obsolete] attribute declarations themselves so the attribute target
                    // doesn't count as a usage of the symbol it marks.
                    if (node.FirstAncestorOrSelf<AttributeSyntax>() is not null)
                        continue;

                    // Avoid counting the same call site multiple times via nested syntax nodes
                    // that all resolve to the same symbol. e.g. `_api.ObsoleteWarning()` yields
                    // an InvocationExpression, a MemberAccessExpression, AND an IdentifierName —
                    // each with a different span — all binding to the same method. Only the
                    // outermost relevant node contributes.
                    if (IsWrappedByOuterRelevantNode(node))
                        continue;

                    var symbol = semanticModel.GetSymbolInfo(node).Symbol;
                    if (symbol is null) continue;

                    // Constructor calls land on IMethodSymbol (the ctor); we want the type's
                    // [Obsolete] flag too, so we accept either the symbol itself or its containing type.
                    var matched = MatchObsoleteSymbol(symbol, targetSet);
                    if (matched is null) continue;

                    var lineSpan = node.GetLocation().GetLineSpan();
                    var file = lineSpan.Path;
                    var line = lineSpan.StartLinePosition.Line + 1;
                    var span = node.Span;

                    if (!seen.Add((file, span))) continue;

                    var callerName = GetCallerName(node);
                    var snippet = node.ToString();

                    usagesByTarget[matched].Add(new ObsoleteUsageSite(
                        CallerName: callerName,
                        FilePath: file,
                        Line: line,
                        Snippet: snippet,
                        Project: projectName,
                        IsGenerated: resolver.IsGenerated(file)));
                }
            }
        }

        // Step 3: build groups, drop zero-usage symbols.
        var groups = new List<ObsoleteSymbolGroup>();
        foreach (var (symbol, data) in obsoleteSymbols)
        {
            var usages = usagesByTarget[symbol];
            if (usages.Count == 0) continue;

            if (errorOnly && !data.IsError) continue;

            usages.Sort((a, b) =>
            {
                var fileCmp = string.CompareOrdinal(a.FilePath, b.FilePath);
                return fileCmp != 0 ? fileCmp : a.Line.CompareTo(b.Line);
            });

            groups.Add(new ObsoleteSymbolGroup(
                SymbolName: symbol.ToDisplayString(),
                DeprecationMessage: data.Message,
                IsError: data.IsError,
                UsageCount: usages.Count,
                Usages: usages));
        }

        // Step 4: sort groups: errors desc, usage count desc, name asc.
        groups.Sort((a, b) =>
        {
            var bySeverity = b.IsError.CompareTo(a.IsError);
            if (bySeverity != 0) return bySeverity;
            var byCount = b.UsageCount.CompareTo(a.UsageCount);
            if (byCount != 0) return byCount;
            return string.CompareOrdinal(a.SymbolName, b.SymbolName);
        });

        return new FindObsoleteUsageResult(groups);
    }

    private record ObsoleteAttributeData(string Message, bool IsError);

    private static ObsoleteAttributeData ParseObsoleteAttribute(AttributeData attr)
    {
        var message = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string s
            ? s
            : string.Empty;
        var isError = attr.ConstructorArguments.Length > 1 && attr.ConstructorArguments[1].Value is bool b && b;
        return new ObsoleteAttributeData(message, isError);
    }

    private static ISymbol? MatchObsoleteSymbol(ISymbol referenced, HashSet<ISymbol> targetSet)
    {
        if (targetSet.Contains(referenced)) return referenced;
        if (targetSet.Contains(referenced.OriginalDefinition)) return referenced.OriginalDefinition;

        // Constructor calls: also match the containing type (when the type itself has [Obsolete]).
        if (referenced is IMethodSymbol m && m.MethodKind == MethodKind.Constructor)
        {
            if (m.ContainingType is not null && targetSet.Contains(m.ContainingType))
                return m.ContainingType;
            if (m.ContainingType?.OriginalDefinition is { } ctorTypeOrig && targetSet.Contains(ctorTypeOrig))
                return ctorTypeOrig;
        }

        return null;
    }

    // Returns true when `node` is a child of another node that the caller will already process
    // and that resolves to the same target symbol. Keeps usage counts at one-per-call-site.
    private static bool IsWrappedByOuterRelevantNode(SyntaxNode node)
    {
        var parent = node.Parent;
        if (parent is null) return false;

        // Wrapped by a member access whose `.Name` is this node:
        //   `_api.ObsoleteWarning` — IdentifierName('ObsoleteWarning') is the .Name of MemberAccess.
        //   The MemberAccess will be visited; skip the IdentifierName.
        if (parent is MemberAccessExpressionSyntax member && member.Name == node)
            return true;

        // Wrapped by member-binding (`obj?.ObsoleteMethod()` — `?.ObsoleteMethod` is a
        // MemberBindingExpression whose .Name is the IdentifierName).
        if (parent is MemberBindingExpressionSyntax binding && binding.Name == node)
            return true;

        // Wrapped by an invocation whose `.Expression` is this node:
        //   `obj.M()` — MemberAccess('obj.M') is the .Expression of Invocation. Invocation wins.
        if (parent is InvocationExpressionSyntax invocation && invocation.Expression == node)
            return true;

        // Wrapped by an object-creation whose `.Type` is this node:
        //   `new ObsoleteType()` — IdentifierName('ObsoleteType') is the .Type. ObjectCreation wins.
        if (parent is ObjectCreationExpressionSyntax creation && creation.Type == node)
            return true;

        // Wrapped by a qualified-name whose `.Right` is this node, when the qualified name itself
        // is the .Type of an object creation:
        //   `new TestLib.ObsoleteSamples.ObsoleteType()` — ObsoleteType IdentifierName is the
        //   .Right of a QualifiedName which is the .Type of ObjectCreation. ObjectCreation wins.
        if (parent is QualifiedNameSyntax qualified
            && qualified.Right == node
            && qualified.Parent is ObjectCreationExpressionSyntax)
            return true;

        // Wrapped by a qualified-name whose `.Right` is this node, when the qualified name's
        // parent is itself wrapped by an outer relevant node (recursive case for deep chains).
        if (parent is QualifiedNameSyntax qualifiedRecurse && qualifiedRecurse.Right == node)
            return IsWrappedByOuterRelevantNode(qualifiedRecurse);

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
