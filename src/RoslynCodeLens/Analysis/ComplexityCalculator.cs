using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Analysis;

public static class ComplexityCalculator
{
    /// <summary>
    /// Computes McCabe cyclomatic complexity for the given syntax node.
    /// Counts: <c>if</c>, each non-<c>default</c> switch label, each non-discard switch-expression
    /// arm, for/foreach/while/do, catch, conditional expression, and the short-circuit operators
    /// (&amp;&amp;, ||, ??).
    /// <para>
    /// <c>else</c> is deliberately NOT counted: it introduces no decision of its own, and an
    /// <c>else if</c> is already counted by its own <see cref="IfStatementSyntax"/>. Counting it
    /// previously made an if/else-if chain score roughly double.
    /// </para>
    /// </summary>
    public static int Calculate(SyntaxNode node)
    {
        var complexity = 1;

        foreach (var descendant in node.DescendantNodes())
        {
            switch (descendant.Kind())
            {
                case SyntaxKind.IfStatement:
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForEachStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.CatchClause:
                case SyntaxKind.ConditionalExpression:
                // Count labels rather than sections: `case 1: case 2:` shares one section but is
                // two decisions, and `default:` (a DefaultSwitchLabelSyntax) is none.
                case SyntaxKind.CaseSwitchLabel:
                case SyntaxKind.CasePatternSwitchLabel:
                    complexity++;
                    break;

                // Switch *expressions* have no sections at all, so they used to be invisible.
                case SyntaxKind.SwitchExpressionArm
                    when ((SwitchExpressionArmSyntax)descendant).Pattern is not DiscardPatternSyntax:
                    complexity++;
                    break;
            }
        }

        foreach (var token in node.DescendantTokens())
        {
#pragma warning disable EPS06
            var kind = token.Kind();
#pragma warning restore EPS06
            switch (kind)
            {
                case SyntaxKind.AmpersandAmpersandToken:
                case SyntaxKind.BarBarToken:
                case SyntaxKind.QuestionQuestionToken:
                    complexity++;
                    break;
            }
        }

        return complexity;
    }
}
