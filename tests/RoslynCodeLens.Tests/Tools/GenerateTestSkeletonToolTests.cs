using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GenerateTestSkeletonToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;

    public GenerateTestSkeletonToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
    }

    [Fact]
    public void Method_GeneratesFactSkeletonForVoidMethod()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Greeter.Dispose",
            framework: "xunit");

        Assert.Equal("XUnit", result.Framework);
        Assert.Equal("GreeterTests", result.ClassName);
        Assert.Contains("[Fact]", result.Code);
        Assert.Contains("public void Dispose_HappyPath()", result.Code);
        Assert.Contains("var sut = new Greeter", result.Code);
        Assert.Contains("using Xunit;", result.Code);
        Assert.Contains("namespace TestLib.Tests", result.Code);
    }

    [Fact]
    public void Type_GeneratesClassWithFactPerPublicMethod()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Greeter",
            framework: "xunit");

        Assert.Equal("GreeterTests", result.ClassName);
        // Greeter has Dispose() — a no-arg void; should appear as a happy-path Fact.
        Assert.Contains("public void Dispose_HappyPath()", result.Code);
    }

    [Fact]
    public void StaticMethod_DoesNotInstantiateSut()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.StaticHelper.Compute",
            framework: "xunit");

        Assert.DoesNotContain("var sut = new", result.Code);
        Assert.Contains("StaticHelper.Compute()", result.Code);
    }

    [Fact]
    public void MethodReturningTask_GeneratesAsyncTest()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.AsyncWorker.DoAsync",
            framework: "xunit");

        Assert.Contains("public async Task DoAsync_HappyPath()", result.Code);
        Assert.Contains("await sut.DoAsync()", result.Code);
        Assert.Contains("using System.Threading.Tasks;", result.Code);
    }

    [Fact]
    public void MethodWithPrimitiveParams_GeneratesTheoryWithInlineData()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Calculator.Add",
            framework: "xunit");

        Assert.Contains("[Theory]", result.Code);
        Assert.Contains("[InlineData(", result.Code);
        Assert.Contains("public void Add_Theory(int a, int b)", result.Code);
    }

    [Fact]
    public void MethodThrowingException_GeneratesAssertThrowsStub()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Validator.Validate",
            framework: "xunit");

        Assert.Contains("Validate_ThrowsArgumentNullException", result.Code);
        Assert.Contains("Validate_ThrowsArgumentException", result.Code);
        Assert.Contains("Assert.Throws<ArgumentNullException>", result.Code);
        Assert.Contains("Assert.Throws<ArgumentException>", result.Code);
    }

    [Fact]
    public void TodoNotes_IncludeConstructorDependencies()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.OrderService",
            framework: "xunit");

        Assert.Contains(
            result.TodoNotes,
            n => n.Contains("IOrderRepo", StringComparison.Ordinal));
        Assert.Contains("/* TODO: dependencies */", result.Code);
    }

    [Fact]
    public void Type_ExcludesPropertiesAndConstructors()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.OrderService",
            framework: "xunit");

        Assert.DoesNotContain("public void .ctor", result.Code);
        Assert.DoesNotContain("get_", result.Code);
        Assert.DoesNotContain("set_", result.Code);
    }

    [Fact]
    public void Framework_AutoDetectsXUnitFromTestProjects()
    {
        // Test solution has 1 each of XUnit / NUnit / MSTest fixtures.
        // Tie → XUnit (enum order: XUnit < NUnit < MSTest).
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Greeter.Dispose",
            framework: null);

        Assert.Equal("XUnit", result.Framework);
    }

    [Fact]
    public void Framework_OverrideHonored()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Greeter.Dispose",
            framework: "nunit");

        Assert.Equal("NUnit", result.Framework);
        Assert.Contains("[Test]", result.Code);
        Assert.Contains("using NUnit.Framework;", result.Code);
    }

    [Fact]
    public void SuggestedPath_TargetsTestProject()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Greeter",
            framework: "xunit");

        // Some test project that references TestLib should be the target.
        Assert.Contains("Tests", result.SuggestedFilePath, StringComparison.Ordinal);
        Assert.EndsWith("GreeterTests.cs", result.SuggestedFilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSymbol_Throws()
    {
        var ex = Assert.Throws<McpToolException>(() =>
            GenerateTestSkeletonLogic.Execute(
                _loaded, _resolver,
                symbol: "TestLib.DoesNotExist",
                framework: "xunit"));

        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Code_IsSyntacticallyValidCSharp()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.OrderService",
            framework: "xunit");

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result.Code);
        var diagnostics = tree.GetDiagnostics().ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void GeneratedCodeSymbol_IsRejected()
    {
        var ex = Assert.Throws<McpToolException>(() =>
            GenerateTestSkeletonLogic.Execute(
                _loaded, _resolver,
                symbol: "TestLib.GeneratedTarget",
                framework: "xunit"));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("generated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThrowStubForParametricMethod_ParsesAsValidCSharp()
    {
        var result = GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver,
            symbol: "TestLib.Validator.Validate",
            framework: "xunit");

        // Validator.Validate(string input) throws ArgumentNullException + ArgumentException;
        // throw stubs must pass an argument, not call sut.Validate() with zero args.
        Assert.Contains("sut.Validate(", result.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("sut.Validate()", result.Code, StringComparison.Ordinal);

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result.Code);
        Assert.Empty(tree.GetDiagnostics());
    }
}
