using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Cognitive complexity and maximum nesting depth, per the SonarSource specification.
/// </summary>
/// <remarks>
/// <para>
/// Cognitive complexity measures how hard code is to <em>understand</em>, where cyclomatic
/// complexity measures how many paths it has. The two differ deliberately:
/// </para>
/// <list type="bullet">
///   <item>A branch-free method scores <b>0</b> here, where cyclomatic scores 1.</item>
///   <item>A <c>switch</c> scores +1 for the whole statement; cyclomatic counts every case.</item>
///   <item>Structures nested inside other structures cost more: +1 plus the current nesting level.</item>
///   <item><c>else</c>/<c>else if</c> cost +1 flat, with no nesting penalty.</item>
///   <item>A boolean <em>sequence</em> costs +1, not +1 per operator: <c>a &amp;&amp; b &amp;&amp; c</c>
///         is +1 while <c>a &amp;&amp; b || c</c> is +2.</item>
///   <item>Lambdas and local functions raise the nesting level but score nothing themselves.</item>
/// </list>
/// <para>
/// This is a purely syntactic analysis — no semantic model. Direct recursion is therefore a
/// <b>heuristic</b>: an invocation is treated as recursive when it is spelled with the member's
/// own name (bare, or via <c>this.</c>). A same-named method reached through a field or a using
/// static would be a false positive. That is accepted for a ranking metric.
/// </para>
/// </remarks>
public static class CognitiveComplexityCalculator
{
    /// <summary>Cognitive complexity and max nesting depth, computed in a single traversal.</summary>
    /// <param name="Cognitive">Cognitive complexity. Starts at 0, not 1.</param>
    /// <param name="MaxNesting">
    /// Deepest control-structure nesting reached, counting the structure itself: a method with a
    /// single <c>if</c> is 1. Lambdas and local functions do not count as control structures, so
    /// their bodies are measured from the depth they sit at.
    /// </param>
    public readonly record struct CognitiveResult(int Cognitive, int MaxNesting);

    /// <summary>Cognitive complexity of <paramref name="node"/>. Zero for a branch-free member.</summary>
    public static int Calculate(SyntaxNode node) => Analyze(node).Cognitive;

    /// <summary>Deepest control-structure nesting inside <paramref name="node"/>.</summary>
    public static int MaxNesting(SyntaxNode node) => Analyze(node).MaxNesting;

    /// <summary>Computes both metrics in one walk. Prefer this over calling both accessors.</summary>
    public static CognitiveResult Analyze(SyntaxNode node)
    {
        var walker = new Walker(SelfName(node));
        foreach (var child in node.ChildNodes())
            walker.Visit(child, nesting: 0, depth: 0);

        return new CognitiveResult(walker.Score + (walker.SawSelfCall ? 1 : 0), walker.MaxDepth);
    }

    /// <summary>
    /// The name a recursive call would be spelled with, or null for members that cannot be
    /// invoked by name (accessors, properties, indexers).
    /// </summary>
    private static string? SelfName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        LocalFunctionStatementSyntax f => f.Identifier.Text,
        _ => null,
    };

    private sealed class Walker(string? selfName)
    {
        public int Score { get; private set; }
        public int MaxDepth { get; private set; }
        public bool SawSelfCall { get; private set; }

        /// <param name="nesting">
        /// Cognitive nesting level — raised by control structures AND by lambdas/local functions.
        /// </param>
        /// <param name="depth">
        /// Structural nesting — raised by control structures only. Drives <see cref="MaxDepth"/>.
        /// </param>
        public void Visit(SyntaxNode node, int nesting, int depth)
        {
            switch (node)
            {
                // `else if` is reached through VisitIf below, never here.
                case IfStatementSyntax ifStatement:
                    VisitIf(ifStatement, nesting, depth, isElseIf: false);
                    return;

                // +1 plus the current nesting level, and everything inside sits one level deeper.
                case ConditionalExpressionSyntax:
                case SwitchStatementSyntax:
                case SwitchExpressionSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                    Score += 1 + nesting;
                    Descend(node, nesting + 1, depth + 1);
                    return;

                // Raise nesting without scoring: the lambda is not itself a decision, but code
                // buried inside one is harder to follow. Not a control structure, so `depth`
                // (and therefore max nesting) is unaffected.
                case SimpleLambdaExpressionSyntax:
                case ParenthesizedLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                case LocalFunctionStatementSyntax:
                    Descend(node, nesting + 1, depth);
                    return;

                // A jump out of the normal flow: +1, no nesting penalty.
                // C# has no labelled break/continue, so `goto` is the whole of this rule here.
                case GotoStatementSyntax:
                    Score++;
                    break;

                // One boolean SEQUENCE, not one per operator. A sequence is a logical binary node
                // whose parent is not a binary node of the same kind, so `a && b && c` — three
                // nodes in Roslyn's tree — is a single +1, while `a && b || c` is two.
                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.LogicalAndExpression)
                      || binary.IsKind(SyntaxKind.LogicalOrExpression):
                    if (node.Parent is not BinaryExpressionSyntax parent || !parent.IsKind(binary.Kind()))
                        Score++;
                    break;

                case InvocationExpressionSyntax invocation when IsSelfCall(invocation):
                    SawSelfCall = true;
                    break;
            }

            Descend(node, nesting, depth);
        }

        /// <summary>
        /// An <c>if</c> scores +1 plus nesting, but an <c>else if</c> does not: its +1 is charged
        /// to the <c>else</c> that introduced it, flat and without a nesting penalty. Chaining is
        /// therefore free of the exponential nesting a naive walk would produce.
        /// </summary>
        private void VisitIf(IfStatementSyntax node, int nesting, int depth, bool isElseIf)
        {
            if (!isElseIf)
                Score += 1 + nesting;

            Visit(node.Condition, nesting, depth);
            Visit(node.Statement, nesting + 1, depth + 1);

            if (node.Else is not { } elseClause)
                return;

            Score++; // `else` and `else if` alike: +1, no nesting penalty.

            if (elseClause.Statement is IfStatementSyntax chained)
                VisitIf(chained, nesting, depth, isElseIf: true);
            else
                Visit(elseClause.Statement, nesting + 1, depth + 1);
        }

        private void Descend(SyntaxNode node, int nesting, int depth)
        {
            if (depth > MaxDepth)
                MaxDepth = depth;

            foreach (var child in node.ChildNodes())
                Visit(child, nesting, depth);
        }

        /// <summary>Heuristic: see the remarks on <see cref="CognitiveComplexityCalculator"/>.</summary>
        private bool IsSelfCall(InvocationExpressionSyntax invocation)
        {
            if (selfName is null)
                return false;

            return invocation.Expression switch
            {
                IdentifierNameSyntax id => string.Equals(id.Identifier.Text, selfName, StringComparison.Ordinal),
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member =>
                    string.Equals(member.Name.Identifier.Text, selfName, StringComparison.Ordinal),
                _ => false,
            };
        }
    }
}
