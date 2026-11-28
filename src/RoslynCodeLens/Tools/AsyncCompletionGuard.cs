using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Recognises a blocking read — <c>x.Result</c> or <c>x.GetAwaiter().GetResult()</c> — that only
/// runs once <c>x</c> is known to be complete, so it neither blocks nor deadlocks. That is the
/// documented fast path for a <c>ValueTask</c>, and find_async_violations must not report it.
/// </summary>
/// <remarks>
/// The check is syntactic and deliberately conservative. The receiver must be a local, parameter
/// or field, named directly or through <c>this.</c>, and the guard must test that same symbol
/// with <c>IsCompleted</c>, <c>IsCompletedSuccessfully</c> or
/// <c>Status == TaskStatus.RanToCompletion</c>. A guard counts when the read sits in the branch it
/// selects: an <c>if</c> or <c>else</c> branch, an arm of a conditional expression, the right
/// operand of <c>&amp;&amp;</c> or <c>||</c>, or the statements after an
/// <c>if (...) return/throw/break/continue;</c> early exit. Anything else is reported as before:
/// another receiver, a guard the branch does not imply, a receiver written to inside the guarded
/// region, or a read inside a lambda or local function, which may run after the guard.
/// </remarks>
internal static class AsyncCompletionGuard
{
    /// <summary>
    /// True when the <c>GetResult()</c> call <paramref name="invocation"/> runs only after its task
    /// or awaiter is known to be complete. The caller has already matched it as
    /// <c>receiver.GetResult()</c> on an awaiter.
    /// </summary>
    public static bool IsGetResultGuarded(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var getResult = (MemberAccessExpressionSyntax)invocation.Expression;

        // An awaiter held in a local: aw.IsCompleted guards aw.GetResult().
        var awaiter = ResolveReceiver(getResult.Expression, model);
        if (awaiter is not null) return IsGuarded(invocation, awaiter, model);

        // x.GetAwaiter().GetResult() or x.ConfigureAwait(...).GetAwaiter().GetResult().
        if (StripInvocation(getResult.Expression, "GetAwaiter") is not { } task) return false;
        task = StripInvocation(task, "ConfigureAwait") ?? task;

        var receiver = ResolveReceiver(task, model);
        return receiver is not null && IsGuarded(invocation, receiver, model);
    }

    /// <summary>
    /// True when the <c>.Result</c> read <paramref name="resultAccess"/> runs only after its task
    /// is known to be complete.
    /// </summary>
    public static bool IsResultGuarded(MemberAccessExpressionSyntax resultAccess, SemanticModel model)
    {
        var receiver = ResolveReceiver(resultAccess.Expression, model);
        return receiver is not null && IsGuarded(resultAccess, receiver, model);
    }

    private static ExpressionSyntax? StripInvocation(ExpressionSyntax expression, string methodName) =>
        expression is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax member,
        } && string.Equals(member.Name.Identifier.Text, methodName, StringComparison.Ordinal)
            ? member.Expression
            : null;

    /// <summary>
    /// The local, parameter or field an expression names directly, or null for anything else.
    /// A property or a call may return a different instance each time, so it never counts.
    /// </summary>
    private static ISymbol? ResolveReceiver(ExpressionSyntax expression, SemanticModel model)
    {
        while (expression is ParenthesizedExpressionSyntax parens)
            expression = parens.Expression;

        var named = expression is IdentifierNameSyntax
                    || expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };
        if (!named) return null;

        var symbol = model.GetSymbolInfo(expression).Symbol;
        return symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol ? symbol : null;
    }

    private static bool IsGuarded(ExpressionSyntax read, ISymbol receiver, SemanticModel model)
    {
        SyntaxNode child = read;
        for (var parent = read.Parent; parent is not null; child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                // The read may run long after any guard around the lambda, by when the captured
                // receiver may have been reassigned.
                case AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or MemberDeclarationSyntax:
                    return false;

                case IfStatementSyntax ifStatement when child == ifStatement.Statement:
                    if (Implies(ifStatement.Condition, receiver, true, model) &&
                        !IsWrittenIn(ifStatement.Statement, receiver, model))
                        return true;
                    break;

                case ElseClauseSyntax { Parent: IfStatementSyntax ifStatement } elseClause:
                    if (Implies(ifStatement.Condition, receiver, false, model) &&
                        !IsWrittenIn(elseClause.Statement, receiver, model))
                        return true;
                    break;

                case ConditionalExpressionSyntax conditional when child != conditional.Condition:
                    if (Implies(conditional.Condition, receiver, child == conditional.WhenTrue, model) &&
                        !IsWrittenIn(child, receiver, model))
                        return true;
                    break;

                case BinaryExpressionSyntax binary when child == binary.Right &&
                                                        binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression:
                    if (Implies(binary.Left, receiver, binary.IsKind(SyntaxKind.LogicalAndExpression), model) &&
                        !IsWrittenIn(binary.Right, receiver, model))
                        return true;
                    break;

                case BlockSyntax block when child is StatementSyntax statement:
                    if (FollowsEarlyExit(block, statement, receiver, model))
                        return true;
                    break;
            }
        }

        return false;
    }

    /// <summary>
    /// True when an earlier statement of <paramref name="block"/> is <c>if (cond) exit;</c>, with
    /// no else, whose condition being false implies the receiver is complete, and nothing from
    /// there up to and including <paramref name="statement"/> writes the receiver.
    /// </summary>
    private static bool FollowsEarlyExit(BlockSyntax block, StatementSyntax statement, ISymbol receiver, SemanticModel model)
    {
        var statements = block.Statements;
        var index = statements.IndexOf(statement);

        for (var i = index - 1; i >= 0; i--)
        {
            // Walking backwards, a write between the exit and the read invalidates every exit
            // above it too.
            if (IsWrittenIn(statements[i + 1], receiver, model)) return false;

            if (statements[i] is IfStatementSyntax { Else: null } exit &&
                AlwaysExits(exit.Statement) &&
                Implies(exit.Condition, receiver, false, model))
                return true;
        }

        return false;
    }

    private static bool AlwaysExits(StatementSyntax statement)
    {
        if (statement is BlockSyntax block)
        {
            if (block.Statements.Count == 0) return false;
            statement = block.Statements[^1];
        }

        return statement is ReturnStatementSyntax or ThrowStatementSyntax
            or BreakStatementSyntax or ContinueStatementSyntax;
    }

    /// <summary>
    /// True when <paramref name="condition"/> evaluating to <paramref name="whenTrue"/> implies the
    /// receiver is complete.
    /// </summary>
    private static bool Implies(ExpressionSyntax condition, ISymbol receiver, bool whenTrue, SemanticModel model)
    {
        switch (condition)
        {
            case ParenthesizedExpressionSyntax parens:
                return Implies(parens.Expression, receiver, whenTrue, model);

            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression):
                return Implies(prefix.Operand, receiver, !whenTrue, model);

            // a && b is true only when both are; a || b is false only when both are.
            case BinaryExpressionSyntax binary when
                (whenTrue && binary.IsKind(SyntaxKind.LogicalAndExpression)) ||
                (!whenTrue && binary.IsKind(SyntaxKind.LogicalOrExpression)):
                return Implies(binary.Left, receiver, whenTrue, model) ||
                       Implies(binary.Right, receiver, whenTrue, model);

            case BinaryExpressionSyntax binary when IsRanToCompletionCheck(binary, receiver, model):
                return binary.IsKind(SyntaxKind.EqualsExpression) == whenTrue;

            case MemberAccessExpressionSyntax member when
                member.Name.Identifier.Text is "IsCompleted" or "IsCompletedSuccessfully":
                return whenTrue && IsReceiver(member.Expression, receiver, model);

            default:
                return false;
        }
    }

    /// <summary><c>x.Status == TaskStatus.RanToCompletion</c> or <c>!=</c>, either way round.</summary>
    private static bool IsRanToCompletionCheck(BinaryExpressionSyntax binary, ISymbol receiver, SemanticModel model)
    {
        if (!binary.IsKind(SyntaxKind.EqualsExpression) && !binary.IsKind(SyntaxKind.NotEqualsExpression))
            return false;

        return (IsStatusOf(binary.Left, receiver, model) && IsRanToCompletion(binary.Right, model)) ||
               (IsStatusOf(binary.Right, receiver, model) && IsRanToCompletion(binary.Left, model));
    }

    private static bool IsStatusOf(ExpressionSyntax expression, ISymbol receiver, SemanticModel model) =>
        expression is MemberAccessExpressionSyntax member &&
        member.Name.Identifier.Text is "Status" &&
        IsReceiver(member.Expression, receiver, model);

    private static bool IsRanToCompletion(ExpressionSyntax expression, SemanticModel model) =>
        model.GetSymbolInfo(expression).Symbol is IFieldSymbol
        {
            Name: "RanToCompletion",
            ContainingType: { } type,
        } &&
        string.Equals(type.ToDisplayString(), "System.Threading.Tasks.TaskStatus", StringComparison.Ordinal);

    private static bool IsReceiver(ExpressionSyntax expression, ISymbol receiver, SemanticModel model) =>
        SymbolEqualityComparer.Default.Equals(ResolveReceiver(expression, model), receiver);

    /// <summary>
    /// True when <paramref name="region"/> assigns the receiver or passes it by ref or out. The
    /// whole region counts, not just the part before the read, because a loop can run a later
    /// write before the read's next iteration.
    /// </summary>
    private static bool IsWrittenIn(SyntaxNode region, ISymbol receiver, SemanticModel model)
    {
        foreach (var node in region.DescendantNodesAndSelf())
        {
            var target = node switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left,
                ArgumentSyntax argument when !argument.RefKindKeyword.IsKind(SyntaxKind.None) &&
                                             !argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword) => argument.Expression,
                _ => null,
            };

            if (target is not null && IsReceiver(target, receiver, model))
                return true;
        }

        return false;
    }
}
