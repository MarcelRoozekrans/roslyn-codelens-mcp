using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests.Analysis;

public class ComplexityCalculatorTests
{
    [Fact]
    public void Calculate_TrivialMethod_ReturnsOne()
    {
        var method = ParseMethod("public void M() { return; }");
        Assert.Equal(1, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_IfStatement_AddsOne()
    {
        var method = ParseMethod("public void M(bool x) { if (x) return; }");
        Assert.Equal(2, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_NestedIfElseAndLoop_CountsAll()
    {
        var method = ParseMethod(@"
            public int M(int x)
            {
                if (x > 0)
                {
                    for (int i = 0; i < x; i++)
                    {
                        if (i % 2 == 0) return i;
                    }
                }
                else
                {
                    return -1;
                }
                return 0;
            }");
        // base 1 + if + for + nested if = 4.
        // Was 5: the `else` was counted as a decision, which it is not.
        Assert.Equal(4, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_ElseIfChain_CountsEachDecisionOnce()
    {
        var method = ParseMethod(@"
            public int M(int a)
            {
                if (a == 1) return 1;
                else if (a == 2) return 2;
                else if (a == 3) return 3;
                else return 4;
            }");
        // 1 + three `if` decisions = 4.
        // Was 7: ElseClause was counted too, so each `else if` scored twice and
        // the bare `else` — which is no decision at all — scored as well.
        Assert.Equal(4, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_BareElse_IsNotADecision()
    {
        var method = ParseMethod("public int M(bool a) { if (a) return 1; else return 2; }");
        Assert.Equal(2, ComplexityCalculator.Calculate(method)); // was 3
    }

    [Fact]
    public void Calculate_SwitchExpression_CountsArmsExceptDiscard()
    {
        var method = ParseMethod("public int M(int a) => a switch { 1 => 1, 2 => 2, _ => 0 };");
        // 1 + two real arms = 3. Was 1 — switch expressions were invisible.
        Assert.Equal(3, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_SwitchStatement_CountsCasesExceptDefault()
    {
        var method = ParseMethod(@"
            public int M(int a)
            {
                switch (a)
                {
                    case 1: return 1;
                    case 2: return 2;
                    default: return 0;
                }
            }");
        // 1 + two cases = 3. Was 4: `default` was counted as a decision.
        Assert.Equal(3, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_MultipleLabelsOnOneSection_CountsEachLabel()
    {
        var method = ParseMethod(@"
            public int M(int a)
            {
                switch (a)
                {
                    case 1:
                    case 2: return 1;
                    default: return 0;
                }
            }");
        // Two case labels share one SwitchSection but are two decisions.
        Assert.Equal(3, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_BooleanShortCircuit_AddsOnePerOperator()
    {
        var method = ParseMethod("public bool M(bool a, bool b, bool c) { return a && b || c; }");
        // base 1 + && + || = 3
        Assert.Equal(3, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Calculate_AccessorDeclaration_WorksOnAccessor()
    {
        var accessor = ParsePropertyGetter(@"
            public int Total
            {
                get
                {
                    if (_x > 0) return _x;
                    return 0;
                }
            }");
        // base 1 + if = 2
        Assert.Equal(2, ComplexityCalculator.Calculate(accessor));
    }

    private static MethodDeclarationSyntax ParseMethod(string code)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {code} }}");
        return tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
    }

    private static AccessorDeclarationSyntax ParsePropertyGetter(string code)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {code} }}");
        return tree.GetRoot().DescendantNodes().OfType<AccessorDeclarationSyntax>().First();
    }
}
