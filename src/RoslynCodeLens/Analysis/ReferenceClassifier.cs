using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Classifies a single reference occurrence into a stable kind string. Pure: no I/O.
/// The semantic model is used only to resolve implicit ref/out parameters at call sites.
/// </summary>
public static class ReferenceClassifier
{
    /// <summary>Every kind this classifier can emit. <c>usage</c> is the unclassifiable fallback.</summary>
    public static readonly IReadOnlyList<string> AllKinds =
    [
        "read", "write", "readwrite", "invocation", "method_group", "object_creation", "cast",
        "type_check", "typeof", "base_type", "type_constraint", "type_argument", "declaration",
        "attribute", "nameof", "xml_doc", "usage",
    ];

    public static string Classify(SyntaxNode node, ISymbol symbol, SemanticModel model)
    {
        var name = node as SimpleNameSyntax ?? node.FirstAncestorOrSelf<SimpleNameSyntax>();
        if (name == null)
            return "usage";

        // nameof(...) and cref both reference a symbol without using it, so they win over
        // the syntactic context they happen to sit in.
        if (IsInsideNameof(name))
            return "nameof";

        if (name.FirstAncestorOrSelf<XmlCrefAttributeSyntax>() != null
            || name.FirstAncestorOrSelf<CrefSyntax>() != null)
            return "xml_doc";

        // Types, type parameters, namespaces and constructors all speak the type vocabulary:
        // the ancestor walk below reads the syntactic position the type name occupies.
        var isTypeLike = symbol is INamedTypeSymbol or ITypeParameterSymbol or INamespaceSymbol
            || symbol is IMethodSymbol { MethodKind: MethodKind.Constructor };

        return isTypeLike
            ? ClassifyTypeContext(name)
            : ClassifyValueContext(name, symbol, model);
    }

    /// <summary>
    /// Walks outward from the type name to the first enclosing node that fixes its meaning.
    /// Innermost-first ordering matters: in <c>new List&lt;Foo&gt;()</c> the argument list is
    /// reached before the object creation, so <c>Foo</c> is a type_argument, not a creation.
    /// </summary>
    private static string ClassifyTypeContext(SimpleNameSyntax name)
    {
        foreach (var ancestor in name.AncestorsAndSelf())
        {
            switch (ancestor)
            {
                case TypeArgumentListSyntax: return "type_argument";
                case AttributeSyntax: return "attribute";
                case BaseObjectCreationExpressionSyntax: return "object_creation";
                case CastExpressionSyntax: return "cast";
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AsExpression): return "cast";
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.IsExpression): return "type_check";
                case PatternSyntax: return "type_check";
                case IsPatternExpressionSyntax: return "type_check";
                case TypeOfExpressionSyntax: return "typeof";
                case BaseTypeSyntax: return "base_type";
                case TypeConstraintSyntax: return "type_constraint";
                case TypeParameterConstraintClauseSyntax: return "type_constraint";
            }

            // Nothing above a statement or member boundary can change a type's role.
            if (ancestor is StatementSyntax or MemberDeclarationSyntax)
                break;
        }

        return "declaration";
    }

    private static string ClassifyValueContext(SimpleNameSyntax name, ISymbol symbol, SemanticModel model)
    {
        // The effective expression: the outermost expression whose value *is* this reference.
        ExpressionSyntax expr = name;
        var climbing = true;
        while (climbing)
        {
            switch (expr.Parent)
            {
                case MemberAccessExpressionSyntax ma when ma.Name == expr:
                    expr = ma;
                    break;
                // For `c?.M()` the binding's parent is already the invocation, so the binding
                // itself is the effective expression — climbing to the parent would skip it.
                case MemberBindingExpressionSyntax mb when mb.Name == expr:
                    expr = mb;
                    break;
                case ElementAccessExpressionSyntax ea when ea.Expression == expr:
                    expr = ea;
                    break;
                case ParenthesizedExpressionSyntax pe:
                    expr = pe;
                    break;
                default:
                    climbing = false;
                    break;
            }
        }

        if (symbol is IMethodSymbol)
        {
            return expr.Parent is InvocationExpressionSyntax inv && inv.Expression == expr
                ? "invocation"
                : "method_group";
        }

        switch (expr.Parent)
        {
            case AssignmentExpressionSyntax assignment when assignment.Left == expr:
                return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? "write" : "readwrite";
            case PrefixUnaryExpressionSyntax pre
                when pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression):
                return "readwrite";
            case PostfixUnaryExpressionSyntax post
                when post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression):
                return "readwrite";
            case ArgumentSyntax arg:
                if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) return "write";
                if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)) return "readwrite";
                return ResolveArgRefKind(arg, model) switch
                {
                    RefKind.Out => "write",
                    RefKind.Ref => "readwrite",
                    _ => "read",
                };
        }

        return "read";
    }

    /// <summary>
    /// The declared ref-kind of the parameter this argument binds to. Named arguments bind by
    /// name and may be reordered, so positional indexing is only valid without a name colon.
    /// </summary>
    private static RefKind ResolveArgRefKind(ArgumentSyntax arg, SemanticModel model)
    {
        if (arg.Parent is not BaseArgumentListSyntax list || list.Parent is null)
            return RefKind.None;

        if (model.GetSymbolInfo(list.Parent).Symbol is not IMethodSymbol invoked)
            return RefKind.None;

        if (arg.NameColon is { Name.Identifier.ValueText: var parameterName })
        {
            var named = invoked.Parameters.FirstOrDefault(
                p => string.Equals(p.Name, parameterName, StringComparison.Ordinal));
            return named?.RefKind ?? RefKind.None;
        }

        var index = list.Arguments.IndexOf(arg);
        if (index < 0 || index >= invoked.Parameters.Length)
            return RefKind.None;

        return invoked.Parameters[index].RefKind;
    }

    private static bool IsInsideNameof(SyntaxNode name)
    {
        for (var ancestor = name.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor is InvocationExpressionSyntax
                    { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } invocation
                && invocation.ArgumentList.Span.Contains(name.Span))
            {
                return true;
            }

            if (ancestor is StatementSyntax or MemberDeclarationSyntax)
                break;
        }

        return false;
    }
}
