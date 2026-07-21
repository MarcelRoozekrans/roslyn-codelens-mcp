using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.TestDiscovery;

namespace RoslynCodeLens.Tools;

public static class FindDisposableMisuseLogic
{
    private const int SnippetMaxLength = 80;

    public static FindDisposableMisuseResult Execute(LoadedSolution loaded, SymbolResolver source)
    {
        // The scanner filters by project NAME, so the test-project set is translated into names.
        // Doing it that way — rather than testing scan.ProjectId inside the loop — matters for a
        // linked file shared between a test project and a production one: the scanner's dedupe is
        // first-one-wins, so a tree rejected after it has already claimed the dedupe slot would be
        // lost from the production project too. Skipping the whole compilation up front cannot.
        var testProjectNames = TestProjectDetector.GetTestProjectIds(loaded.Solution)
            .Select(source.GetProjectName)
            .ToHashSet(StringComparer.Ordinal);

        var violations = new List<DisposableMisuseViolation>();

        // Which trees to walk — once each, generated ones skipped, project attribution
        // deterministic — is SolutionScanner's job; this loop only asks what each node is.
        // Before this, every tree present in more than one compilation (a linked file, or one
        // project multi-targeted across frameworks) was walked once per compilation and its
        // violations counted that many times.
        foreach (var scan in SolutionScanner.EnumerateTrees(
                     loaded, source, projectFilter: name => !testProjectNames.Contains(name)))
        {
            var projectName = scan.ProjectName;
            var idisposable = scan.Compilation.GetTypeByMetadataName("System.IDisposable");
            var iasyncDisposable = scan.Compilation.GetTypeByMetadataName("System.IAsyncDisposable");

            if (idisposable is null && iasyncDisposable is null) continue;

            // The model must come from the tree's OWN compilation, which is what the scan hands
            // back; binding a tree against any other would resolve nothing.
            var semanticModel = scan.SemanticModel();

            foreach (var methodDecl in scan.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol is null || methodSymbol.IsImplicitlyDeclared) continue;

                var containingMethodName = methodSymbol.ContainingType is null
                    ? methodSymbol.Name
                    : $"{methodSymbol.ContainingType.Name}.{methodSymbol.Name}";

                var body = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
                if (body is null) continue;

                AnalyzeBody(body, semanticModel, idisposable, iasyncDisposable,
                    containingMethodName, projectName, violations);
            }
        }

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

        var summary = new DisposableMisuseSummary(
            TotalViolations: violations.Count,
            ByPattern: byPattern,
            BySeverity: bySeverity);

        return new FindDisposableMisuseResult(summary, violations);
    }

    private static void AnalyzeBody(
        SyntaxNode body,
        SemanticModel semanticModel,
        INamedTypeSymbol? idisposable,
        INamedTypeSymbol? iasyncDisposable,
        string containingMethodName,
        string projectName,
        List<DisposableMisuseViolation> violations)
    {
        // Pattern 1: local declaration not disposed
        foreach (var localDecl in body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            // Skip `using var x = ...;` declarations.
            if (!localDecl.UsingKeyword.IsKind(SyntaxKind.None))
                continue;

            foreach (var declarator in localDecl.Declaration.Variables)
            {
                if (declarator.Initializer is null) continue;

                var typeInfo = semanticModel.GetTypeInfo(declarator.Initializer.Value);
                if (!ImplementsDisposable(typeInfo.Type, idisposable, iasyncDisposable))
                    continue;

                var variableName = declarator.Identifier.Text;
                if (IsDisposalAcknowledged(body, variableName, semanticModel))
                    continue;

                violations.Add(BuildViolation(
                    DisposableMisusePattern.DisposableNotDisposed,
                    DisposableMisuseSeverity.Warning,
                    declarator.GetLocation(),
                    containingMethodName,
                    projectName,
                    Snippet(declarator.ToString())));
            }
        }

        // Pattern 2: bare-expression-statement discard
        foreach (var stmt in body.DescendantNodes().OfType<ExpressionStatementSyntax>())
        {
            if (stmt.Expression is not (ObjectCreationExpressionSyntax or InvocationExpressionSyntax)) continue;

            var typeInfo = semanticModel.GetTypeInfo(stmt.Expression);
            if (!ImplementsDisposable(typeInfo.Type, idisposable, iasyncDisposable))
                continue;

            violations.Add(BuildViolation(
                DisposableMisusePattern.DisposableDiscarded,
                DisposableMisuseSeverity.Error,
                stmt.GetLocation(),
                containingMethodName,
                projectName,
                Snippet(stmt.Expression.ToString())));
        }
    }

    private static bool ImplementsDisposable(
        ITypeSymbol? type,
        INamedTypeSymbol? idisposable,
        INamedTypeSymbol? iasyncDisposable)
    {
        if (type is null) return false;

        if (idisposable is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(type, idisposable)) return true;
            foreach (var iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, idisposable)) return true;
            }
        }

        if (iasyncDisposable is not null)
        {
            if (SymbolEqualityComparer.Default.Equals(type, iasyncDisposable)) return true;
            foreach (var iface in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(iface, iasyncDisposable)) return true;
            }
        }

        return false;
    }

    private static bool IsDisposalAcknowledged(SyntaxNode body, string variableName, SemanticModel semanticModel)
    {
        foreach (var node in body.DescendantNodes())
        {
            // return x;  or  return (X)x;  or  return Wrap(x);
            if (node is ReturnStatementSyntax returnStmt && returnStmt.Expression is not null)
            {
                if (ContainsIdentifier(returnStmt.Expression, variableName))
                    return true;
            }

            if (node is AssignmentExpressionSyntax assignment &&
                assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                if (!ContainsIdentifier(assignment.Right, variableName))
                    continue;

                // LHS is `this.field` / `someInstance.field` — ownership transferred to instance.
                if (assignment.Left is MemberAccessExpressionSyntax)
                    return true;

                // LHS is a bare identifier — check if it resolves to an out/ref parameter or a field/property.
                if (assignment.Left is IdentifierNameSyntax leftId)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(leftId);
                    if (symbolInfo.Symbol is IParameterSymbol param &&
                        param.RefKind is RefKind.Out or RefKind.Ref)
                    {
                        return true;
                    }
                    if (symbolInfo.Symbol is IFieldSymbol or IPropertySymbol)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool ContainsIdentifier(SyntaxNode node, string name)
    {
        if (node is IdentifierNameSyntax id && id.Identifier.Text == name)
            return true;

        foreach (var descendant in node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (descendant.Identifier.Text == name)
                return true;
        }

        return false;
    }

    private static string Snippet(string source)
    {
        if (source.Length <= SnippetMaxLength) return source;
        return source[..SnippetMaxLength] + "...";
    }

    private static DisposableMisuseViolation BuildViolation(
        DisposableMisusePattern pattern,
        DisposableMisuseSeverity severity,
        Location location,
        string containingMethod,
        string projectName,
        string snippet)
    {
        var span = location.GetLineSpan();
        return new DisposableMisuseViolation(
            Pattern: pattern,
            Severity: severity,
            FilePath: span.Path ?? string.Empty,
            Line: span.StartLinePosition.Line + 1,
            ContainingMethod: containingMethod,
            Project: projectName,
            Snippet: snippet);
    }
}
