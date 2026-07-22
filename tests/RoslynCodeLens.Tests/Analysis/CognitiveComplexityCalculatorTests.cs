using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests.Analysis;

public class CognitiveComplexityCalculatorTests
{
    [Fact]
    public void Nesting_IsPenalised()
    {
        var method = ParseMethod(@"
            public void M(int[] xs)
            {
                foreach (var x in xs)      // +1 (nesting 0)
                    if (x > 0)             // +2 (nesting 1)
                        while (x > 1)      // +3 (nesting 2)
                            System.Console.Write(x);
            }");
        // 6 — while cyclomatic scores this 4. This pair is the whole point of the
        // metric: a test where both agree cannot detect a missing nesting penalty.
        Assert.Equal(6, CognitiveComplexityCalculator.Calculate(method));
        Assert.Equal(4, ComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void ElseIf_AddsOne_ButNoNestingPenalty()
    {
        var method = ParseMethod(@"
            public int M(int a)
            {
                if (a == 1) return 1;      // +1
                else if (a == 2) return 2; // +1
                else return 3;             // +1
            }");
        Assert.Equal(3, CognitiveComplexityCalculator.Calculate(method));
    }

    [Theory]
    [InlineData("a && b && c", 1)]        // one sequence
    [InlineData("a && b || c", 2)]        // two sequences
    [InlineData("a && b && c || d", 2)]
    [InlineData("a || b", 1)]
    public void BooleanSequences_CountOncePerSequence(string expr, int expected)
    {
        var method = ParseMethod($"public bool M(bool a, bool b, bool c, bool d) => {expr};");
        Assert.Equal(expected, CognitiveComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Lambda_RaisesNesting_ButDoesNotScoreItself()
    {
        var method = ParseMethod(@"
            public void M(System.Collections.Generic.List<int> xs)
            {
                xs.ForEach(x => { if (x > 0) System.Console.Write(x); });
            }");
        // The lambda itself is +0; the `if` inside it sits at nesting 1, so +2.
        Assert.Equal(2, CognitiveComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void LocalFunction_RaisesNesting_ButDoesNotScoreItself()
    {
        var method = ParseMethod(@"
            public void M()
            {
                void Inner(int y) { if (y > 0) System.Console.Write(y); }
                Inner(1);
            }");
        Assert.Equal(2, CognitiveComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Catch_IsPenalisedByNesting()
    {
        var method = ParseMethod(@"
            public void M()
            {
                try { }
                catch (System.Exception) { }   // +1
                finally { }                    // finally is not a branch
            }");
        Assert.Equal(1, CognitiveComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Switch_ScoresOnce_NotPerCase()
    {
        var method = ParseMethod(@"
            public int M(int a)
            {
                switch (a)                 // +1 for the whole switch
                {
                    case 1: return 1;
                    case 2: return 2;
                    default: return 0;
                }
            }");
        // Cognitive treats one switch as ONE decision to understand, unlike cyclomatic.
        Assert.Equal(1, CognitiveComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void Goto_AddsOne()
    {
        var method = ParseMethod(@"
            public void M(int a)
            {
                if (a > 0) goto End;    // +1 if, +1 goto
                End: ;
            }");
        Assert.Equal(2, CognitiveComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void DirectRecursion_AddsOne()
    {
        var method = ParseMethod("public int F(int n) => n < 2 ? n : F(n - 1) + F(n - 2);");
        // +1 ternary, +1 recursion (counted once however many recursive calls).
        Assert.Equal(2, CognitiveComplexityCalculator.Calculate(method));
    }

    [Fact]
    public void TrivialMethod_IsZero()
    {
        // Cognitive complexity starts at ZERO, unlike cyclomatic's 1 — a method with
        // no branching costs nothing to understand.
        Assert.Equal(0, CognitiveComplexityCalculator.Calculate(ParseMethod("public void M() { }")));
    }

    private static MethodDeclarationSyntax ParseMethod(string code)
    {
        var tree = CSharpSyntaxTree.ParseText($"class C {{ {code} }}");
        return tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
    }
}
