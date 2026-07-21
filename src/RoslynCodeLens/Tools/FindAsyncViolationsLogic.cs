using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindAsyncViolationsLogic
{
    private const int SnippetMaxLength = 80;

    public static FindAsyncViolationsResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        // The scanner filters by project NAME, so the test-project set is translated into names.
        // Doing it that way — rather than testing scan.ProjectId inside the loop — matters for a
        // linked file shared between a test project and a production one: the scanner's dedupe is
        // first-one-wins, so a tree rejected after it has already claimed the dedupe slot would be
        // lost from the production project too. Skipping the whole compilation up front cannot.
        var testProjectNames = TestProjectDetector.GetTestProjectIds(loaded.Solution)
            .Select(source.GetProjectName)
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<AsyncViolation>();

        // Which trees to walk — once each, generated ones skipped, project attribution
        // deterministic — is SolutionScanner's job; this loop only asks what each node is.
        // Before this, every tree present in more than one compilation (a linked file, or one
        // project multi-targeted across frameworks) was walked once per compilation and its
        // violations counted that many times.
        foreach (var scan in SolutionScanner.EnumerateTrees(
                     loaded, source, projectFilter: name => !testProjectNames.Contains(name)))
        {
            var compilation = scan.Compilation;
            var projectName = scan.ProjectName;
            var taskSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var taskGenericSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            var valueTaskSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
            var valueTaskGenericSymbol = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
            var eventArgsSymbol = compilation.GetTypeByMetadataName("System.EventArgs");

            if (taskSymbol is null) continue;

            // The model must come from the tree's OWN compilation, which is what the scan hands
            // back; binding a tree against any other would resolve nothing.
            var semanticModel = scan.SemanticModel();

            foreach (var methodDecl in scan.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol is null || methodSymbol.IsImplicitlyDeclared)
                    continue;

                var containingMethodName = methodSymbol.ContainingType is null
                    ? methodSymbol.Name
                    : $"{methodSymbol.ContainingType.Name}.{methodSymbol.Name}";

                // Pattern 4: AsyncVoid
                if (IsAsyncVoid(methodDecl) && !IsEventHandlerShaped(methodSymbol, eventArgsSymbol))
                {
                    violations.Add(BuildViolation(
                        AsyncViolationPattern.AsyncVoid,
                        AsyncViolationSeverity.Error,
                        methodDecl.Identifier.GetLocation(),
                        containingMethodName,
                        projectName,
                        "async void " + methodDecl.Identifier.Text + "(...)"));
                }

                var body = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
                if (body is null) continue;

                var isAsync = methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword);

                // Patterns 1-3: walk member-access + invocation expressions in the body
                foreach (var node in body.DescendantNodes())
                {
                    if (node is MemberAccessExpressionSyntax memberAccess &&
                        memberAccess.Name.Identifier.Text == "Result" &&
                        memberAccess.Parent is not InvocationExpressionSyntax)
                    {
                        var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
                        if (HasTaskResultProperty(receiverType, taskSymbol, taskGenericSymbol, valueTaskGenericSymbol))
                        {
                            violations.Add(BuildViolation(
                                AsyncViolationPattern.SyncOverAsyncResult,
                                AsyncViolationSeverity.Error,
                                memberAccess.GetLocation(),
                                containingMethodName,
                                projectName,
                                Snippet(memberAccess.ToString())));
                        }
                    }
                    else if (node is InvocationExpressionSyntax invocation)
                    {
                        var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                        if (symbol is null) continue;

                        if (IsTaskWaitMethod(symbol, taskSymbol))
                        {
                            violations.Add(BuildViolation(
                                AsyncViolationPattern.SyncOverAsyncWait,
                                AsyncViolationSeverity.Error,
                                invocation.GetLocation(),
                                containingMethodName,
                                projectName,
                                Snippet(invocation.ToString())));
                        }
                        else if (IsGetResultOnAwaiter(invocation, symbol))
                        {
                            violations.Add(BuildViolation(
                                AsyncViolationPattern.SyncOverAsyncGetAwaiterGetResult,
                                AsyncViolationSeverity.Error,
                                invocation.GetLocation(),
                                containingMethodName,
                                projectName,
                                Snippet(invocation.ToString())));
                        }
                    }
                }

                // Patterns 5 & 6: bare expression statement of Task-returning invocation
                foreach (var stmt in body.DescendantNodes().OfType<ExpressionStatementSyntax>())
                {
                    if (stmt.Expression is not InvocationExpressionSyntax invocation)
                        continue;

                    var symbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (symbol is null) continue;

                    if (!IsTaskOrValueTaskType(symbol.ReturnType, taskSymbol, taskGenericSymbol, valueTaskSymbol, valueTaskGenericSymbol))
                        continue;

                    var pattern = isAsync
                        ? AsyncViolationPattern.MissingAwait
                        : AsyncViolationPattern.FireAndForget;

                    violations.Add(BuildViolation(
                        pattern,
                        AsyncViolationSeverity.Warning,
                        invocation.GetLocation(),
                        containingMethodName,
                        projectName,
                        Snippet(invocation.ToString())));
                }
            }
        }

        // Sort: severity ASC (Error=0 first), then file ASC, then line ASC.
        violations.Sort((a, b) =>
        {
            var bySeverity = ((int)a.Severity).CompareTo((int)b.Severity);
            if (bySeverity != 0) return bySeverity;
            var byPath = string.CompareOrdinal(a.FilePath, b.FilePath);
            if (byPath != 0) return byPath;
            return a.Line.CompareTo(b.Line);
        });

        var byPattern = violations
            .GroupBy(v => v.Pattern.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var bySeverity = violations
            .GroupBy(v => v.Severity.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var summary = new AsyncViolationSummary(
            TotalViolations: violations.Count,
            ByPattern: byPattern,
            BySeverity: bySeverity);

        return new FindAsyncViolationsResult(summary, violations);
    }

    private static bool IsAsyncVoid(MethodDeclarationSyntax methodDecl)
    {
        if (!methodDecl.Modifiers.Any(SyntaxKind.AsyncKeyword)) return false;
        return methodDecl.ReturnType is PredefinedTypeSyntax pts &&
               pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
    }

    private static bool IsEventHandlerShaped(IMethodSymbol method, INamedTypeSymbol? eventArgsSymbol)
    {
        if (eventArgsSymbol is null) return false;
        if (method.Parameters.Length != 2) return false;

        var firstParam = method.Parameters[0].Type;
        var firstOk = firstParam.SpecialType == SpecialType.System_Object ||
                      firstParam.TypeKind == TypeKind.Class ||
                      firstParam.TypeKind == TypeKind.Interface;
        if (!firstOk) return false;

        return InheritsFromOrIs(method.Parameters[1].Type, eventArgsSymbol);
    }

    private static bool InheritsFromOrIs(ITypeSymbol type, INamedTypeSymbol baseType)
    {
        var current = type as INamedTypeSymbol;
        while (current is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
            current = current.BaseType;
        }
        return false;
    }

    private static bool HasTaskResultProperty(ITypeSymbol? type, INamedTypeSymbol taskSymbol, INamedTypeSymbol? taskGenericSymbol, INamedTypeSymbol? valueTaskGenericSymbol)
    {
        if (type is null) return false;
        if (SymbolEqualityComparer.Default.Equals(type, taskSymbol)) return true;
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var ctor = named.ConstructedFrom;
            if (taskGenericSymbol is not null &&
                SymbolEqualityComparer.Default.Equals(ctor, taskGenericSymbol)) return true;
            if (valueTaskGenericSymbol is not null &&
                SymbolEqualityComparer.Default.Equals(ctor, valueTaskGenericSymbol)) return true;
        }
        return false;
    }

    private static bool IsTaskOrValueTaskType(
        ITypeSymbol type,
        INamedTypeSymbol taskSymbol,
        INamedTypeSymbol? taskGenericSymbol,
        INamedTypeSymbol? valueTaskSymbol,
        INamedTypeSymbol? valueTaskGenericSymbol)
    {
        if (SymbolEqualityComparer.Default.Equals(type, taskSymbol)) return true;
        if (valueTaskSymbol is not null && SymbolEqualityComparer.Default.Equals(type, valueTaskSymbol)) return true;

        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var ctor = named.ConstructedFrom;
            if (taskGenericSymbol is not null &&
                SymbolEqualityComparer.Default.Equals(ctor, taskGenericSymbol)) return true;
            if (valueTaskGenericSymbol is not null &&
                SymbolEqualityComparer.Default.Equals(ctor, valueTaskGenericSymbol)) return true;
        }
        return false;
    }

    private static bool IsTaskWaitMethod(IMethodSymbol method, INamedTypeSymbol taskSymbol)
    {
        var containing = method.ContainingType?.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(containing, taskSymbol)) return false;
        return method.Name is "Wait" or "WaitAll" or "WaitAny";
    }

    private static bool IsGetResultOnAwaiter(InvocationExpressionSyntax invocation, IMethodSymbol symbol)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
        if (memberAccess.Name.Identifier.Text != "GetResult") return false;

        var containingType = symbol.ContainingType?.OriginalDefinition;
        if (containingType is null) return false;

        var fqn = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

        return fqn is "System.Runtime.CompilerServices.TaskAwaiter"
            or "System.Runtime.CompilerServices.TaskAwaiter<TResult>"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredTaskAwaitable<TResult>.ConfiguredTaskAwaiter"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter"
            or "System.Runtime.CompilerServices.ValueTaskAwaiter<TResult>"
            or "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter"
            or "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<TResult>.ConfiguredValueTaskAwaiter";
    }

    private static string Snippet(string source)
    {
        if (source.Length <= SnippetMaxLength) return source;
        return source[..SnippetMaxLength] + "...";
    }

    private static AsyncViolation BuildViolation(
        AsyncViolationPattern pattern,
        AsyncViolationSeverity severity,
        Location location,
        string containingMethod,
        string projectName,
        string snippet)
    {
        var span = location.GetLineSpan();
        return new AsyncViolation(
            Pattern: pattern,
            Severity: severity,
            FilePath: span.Path ?? string.Empty,
            Line: span.StartLinePosition.Line + 1,
            ContainingMethod: containingMethod,
            Project: projectName,
            Snippet: snippet);
    }
}
