using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests;

public class ExceptionAnalyzerTests
{
    private static (MethodDeclarationSyntax Method, SemanticModel Model) Compile(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create(
            "C",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == methodName);
        return (method, comp.GetSemanticModel(tree));
    }

    private static string Wrap(string members) => $"class C {{ {members} }}";

    private static List<string> ThrownTypes(string source, string methodName)
    {
        var (method, model) = Compile(source, methodName);
        return ExceptionAnalyzer.CollectThrowSites(method, model)
            .Select(s => s.ExceptionType.Name).ToList();
    }

    private static (ThrowSite Site, SemanticModel Model) SingleSite(string source, string methodName)
    {
        var (method, model) = Compile(source, methodName);
        return (ExceptionAnalyzer.CollectThrowSites(method, model).Single(), model);
    }

    [Fact]
    public void DirectThrow_IsCollected()
    {
        var types = ThrownTypes(Wrap("void M() { throw new System.InvalidOperationException(); }"), "M");
        Assert.Equal(["InvalidOperationException"], types);
    }

    [Fact]
    public void ThrowExpression_IsCollected()
    {
        var types = ThrownTypes(
            Wrap("string M(string s) => s ?? throw new System.ArgumentNullException();"), "M");
        Assert.Equal(["ArgumentNullException"], types);
    }

    [Fact]
    public void ThrowVariable_UsesStaticType()
    {
        var types = ThrownTypes(
            Wrap("void M() { var e = new System.IO.IOException(); throw e; }"), "M");
        Assert.Equal(["IOException"], types);
    }

    [Fact]
    public void Rethrow_TakesEnclosingCatchType()
    {
        var (site, _) = SingleSite(
            Wrap("void M() { try { } catch (System.IO.IOException) { throw; } }"), "M");
        Assert.Equal("IOException", site.ExceptionType.Name);
        Assert.True(site.IsRethrow);
    }

    [Fact]
    public void BareRethrow_IsException()
    {
        var (site, _) = SingleSite(Wrap("void M() { try { } catch { throw; } }"), "M");
        Assert.Equal("Exception", site.ExceptionType.Name);
        Assert.True(site.IsRethrow);
    }

    [Fact]
    public void ThrowInLambda_IsNotCollected()
    {
        var types = ThrownTypes(
            Wrap("void M() { System.Action a = () => throw new System.Exception(); a(); }"), "M");
        Assert.Empty(types);
    }

    [Fact]
    public void ThrowInLocalFunction_IsNotCollected()
    {
        var types = ThrownTypes(
            Wrap("void M() { void Inner() => throw new System.Exception(); Inner(); }"), "M");
        Assert.Empty(types);
    }

    [Fact]
    public void CatchesType_ExactAndBase()
    {
        var (method, model) = Compile(
            Wrap("void M() { try { } catch (System.Exception) { } try { } catch (System.IO.IOException) { } }"),
            "M");
        var clauses = method.DescendantNodes().OfType<CatchClauseSyntax>().ToList();
        var argNull = model.Compilation.GetTypeByMetadataName("System.ArgumentNullException")!;

        Assert.True(ExceptionAnalyzer.CatchesType(clauses[0], argNull, model));
        Assert.False(ExceptionAnalyzer.CatchesType(clauses[1], argNull, model));
    }

    [Fact]
    public void CatchesType_BareCatch()
    {
        var (method, model) = Compile(Wrap("void M() { try { } catch { } }"), "M");
        var clause = method.DescendantNodes().OfType<CatchClauseSyntax>().Single();
        var argNull = model.Compilation.GetTypeByMetadataName("System.ArgumentNullException")!;

        Assert.True(ExceptionAnalyzer.CatchesType(clause, argNull, model));
    }

    [Fact]
    public void EnumerateHandlers_CatchesInSameMethod()
    {
        var (site, model) = SingleSite(
            Wrap("void M() { try { throw new System.IO.IOException(); } catch (System.IO.IOException) { } }"),
            "M");

        var handler = Assert.Single(
            ExceptionAnalyzer.EnumerateHandlers(site.Node, site.ExceptionType, model));

        Assert.False(handler.HasFilter);
    }

    [Fact]
    public void EnumerateHandlers_FilteredCatch_ReportsFilter()
    {
        var (site, model) = SingleSite(
            Wrap("void M() { try { throw new System.IO.IOException(); } catch (System.IO.IOException) when (true) { } }"),
            "M");

        var handler = Assert.Single(
            ExceptionAnalyzer.EnumerateHandlers(site.Node, site.ExceptionType, model));

        Assert.True(handler.HasFilter);
    }

    [Fact]
    public void EnumerateHandlers_FinallyDoesNotCatch()
    {
        var (site, model) = SingleSite(
            Wrap("void M() { try { throw new System.IO.IOException(); } finally { } }"), "M");

        Assert.Empty(ExceptionAnalyzer.EnumerateHandlers(site.Node, site.ExceptionType, model));
    }

    [Fact]
    public void EnumerateHandlers_ThrowInsideCatchNotCaughtByOwnTry()
    {
        var (method, model) = Compile(
            Wrap("void M() { try { } catch (System.Exception) { throw new System.IO.IOException(); } }"),
            "M");
        // The rethrow-free throw inside the catch body is the only non-rethrow site here.
        var site = ExceptionAnalyzer.CollectThrowSites(method, model).Single(s => !s.IsRethrow);

        Assert.Empty(ExceptionAnalyzer.EnumerateHandlers(site.Node, site.ExceptionType, model));
    }

    [Fact]
    public void EnumerateHandlers_StopsAtMethodBoundary()
    {
        const string Source = """
            class C
            {
                void Thrower() { throw new System.IO.IOException(); }
                void Guarded() { try { Thrower(); } catch (System.IO.IOException) { } }
            }
            """;
        var (site, model) = SingleSite(Source, "Thrower");

        Assert.Empty(ExceptionAnalyzer.EnumerateHandlers(site.Node, site.ExceptionType, model));
    }

    [Fact]
    public void EnumerateHandlers_YieldsEveryBindingClauseInSourceOrder()
    {
        var (site, model) = SingleSite(
            Wrap("""
                void M()
                {
                    try { throw new System.IO.IOException(); }
                    catch (System.IO.IOException) when (true) { }
                    catch (System.IO.IOException) { }
                }
                """),
            "M");

        var handlers = ExceptionAnalyzer.EnumerateHandlers(site.Node, site.ExceptionType, model)
            .ToList();

        Assert.Equal([true, false], handlers.Select(h => h.HasFilter));
    }

    [Fact]
    public void EnumerateHandlers_WalksOutwardFromInnermostTry()
    {
        var (site, model) = SingleSite(
            Wrap("""
                void M()
                {
                    try
                    {
                        try { throw new System.IO.IOException(); }
                        catch (System.IO.IOException) when (true) { }
                    }
                    catch (System.IO.IOException) { }
                }
                """),
            "M");

        var handlers = ExceptionAnalyzer.EnumerateHandlers(site.Node, site.ExceptionType, model)
            .ToList();

        Assert.Equal([true, false], handlers.Select(h => h.HasFilter));
    }

    [Fact]
    public void CatchesType_MatchesConstructedGenericExceptions()
    {
        // GetTypeByMetadataName-style localisation hands back the UNBOUND definition, which is
        // never SymbolEqualityComparer-equal to the constructed type — hence FQN comparison.
        const string Source = """
            class MyEx<T> : System.Exception { }

            class C
            {
                void M()
                {
                    try { throw new MyEx<string>(); }
                    catch (MyEx<string>) { }
                }

                void Other()
                {
                    try { throw new MyEx<string>(); }
                    catch (MyEx<int>) { }
                }
            }
            """;

        var (matching, matchingModel) = SingleSite(Source, "M");
        Assert.Single(
            ExceptionAnalyzer.EnumerateHandlers(matching.Node, matching.ExceptionType, matchingModel));

        var (mismatched, mismatchedModel) = SingleSite(Source, "Other");
        Assert.Empty(
            ExceptionAnalyzer.EnumerateHandlers(
                mismatched.Node, mismatched.ExceptionType, mismatchedModel));
    }

    [Fact]
    public void ParseExceptionCrefs_ReadsTags()
    {
        const string Xml =
            """<member><exception cref="T:System.IO.IOException">no</exception><exception cref="T:System.ArgumentNullException">bad</exception></member>""";

        var crefs = ExceptionAnalyzer.ParseExceptionCrefs(Xml);

        Assert.Equal(["System.IO.IOException", "System.ArgumentNullException"], crefs);
    }
}
