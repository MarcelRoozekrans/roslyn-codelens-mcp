using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class GetComplexityMetricsLogic
{
    /// <param name="threshold">
    /// Minimum score to report, applied to whichever metric <paramref name="metric"/> selects.
    /// </param>
    /// <param name="metric">
    /// Which number <paramref name="threshold"/> filters on and the results are sorted by. Both
    /// numbers are reported either way. Defaults to cyclomatic so existing callers are unaffected.
    /// </param>
    public static IReadOnlyList<ComplexityMetric> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        string? project,
        int threshold,
        ComplexityMetricKind metric = ComplexityMetricKind.Cyclomatic)
    {
        var results = new List<ComplexityMetric>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);

            if (project != null &&
                !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
                {
                    var bodies = ScoredBodies(member);
                    if (bodies.Count == 0)
                        continue;

                    // Max across a property's accessors, per metric: the property is one row and
                    // the worst accessor is what a reader has to deal with.
                    var complexity = bodies.Max(ComplexityCalculator.Calculate);
                    var cognitive = 0;
                    var maxNesting = 0;
                    foreach (var body in bodies)
                    {
                        var analysis = CognitiveComplexityCalculator.Analyze(body);
                        cognitive = Math.Max(cognitive, analysis.Cognitive);
                        maxNesting = Math.Max(maxNesting, analysis.MaxNesting);
                    }

                    var selected = metric == ComplexityMetricKind.Cognitive ? cognitive : complexity;
                    if (selected < threshold)
                        continue;

                    var symbol = semanticModel.GetDeclaredSymbol(member);
                    var lineSpan = member.GetLocation().GetLineSpan();

                    results.Add(new ComplexityMetric(
                        MemberName(member, symbol),
                        symbol?.ContainingType?.Name ?? "Unknown",
                        complexity,
                        cognitive,
                        maxNesting,
                        lineSpan.Path ?? "",
                        lineSpan.StartLinePosition.Line + 1,
                        projectName));
                }
            }
        }

        return results.OrderByDescending(r => Score(r, metric)).ToList();
    }

    /// <summary>The selected metric's value for one row.</summary>
    internal static int Score(ComplexityMetric metric, ComplexityMetricKind kind)
        => kind == ComplexityMetricKind.Cognitive ? metric.Cognitive : metric.Complexity;

    /// <summary>
    /// The syntax to score for a member, or an empty list for members that hold no code (fields,
    /// type declarations, using directives...). Lambdas and local functions are not members, so
    /// they are scored as part of the member that contains them rather than becoming their own
    /// rows — the question this tool answers is "which member do I refactor".
    /// </summary>
    private static IReadOnlyList<SyntaxNode> ScoredBodies(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax
            or ConstructorDeclarationSyntax
            or DestructorDeclarationSyntax
            or OperatorDeclarationSyntax
            or ConversionOperatorDeclarationSyntax => [member],

        // A property or indexer is one row scored by its worst accessor. Accessors without a body
        // (an auto-property) hold no code, so such a member yields nothing rather than a row of 1.
        PropertyDeclarationSyntax { ExpressionBody: { } body } => [body],
        IndexerDeclarationSyntax { ExpressionBody: { } body } => [body],
        BasePropertyDeclarationSyntax { AccessorList: { } accessors } and (PropertyDeclarationSyntax or IndexerDeclarationSyntax) =>
            [.. accessors.Accessors.Where(a => a.Body is not null || a.ExpressionBody is not null)],

        _ => [],
    };

    /// <summary>
    /// A constructor's symbol is named <c>.ctor</c>, which is not what a caller wants to read; the
    /// other tools render one as its type name, so this does too.
    /// </summary>
    private static string MemberName(MemberDeclarationSyntax member, ISymbol? symbol) => member switch
    {
        ConstructorDeclarationSyntax ctor => ctor.Identifier.Text,
        DestructorDeclarationSyntax dtor => $"~{dtor.Identifier.Text}",
        _ => symbol?.Name ?? (member as MethodDeclarationSyntax)?.Identifier.Text ?? "Unknown",
    };
}
