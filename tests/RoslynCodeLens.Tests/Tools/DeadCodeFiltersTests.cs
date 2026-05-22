using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class DeadCodeFiltersTests
{
    [Fact]
    public void Classify_PlainMethod_ReturnsNone()
    {
        var method = GetMethod("class C { public void M() {} }", "C", "M");
        Assert.Equal(DeadCodeFilters.Reason.None, DeadCodeFilters.Classify(method));
    }

    [Theory]
    [InlineData("Fact")]
    [InlineData("Theory")]
    [InlineData("Test")]
    [InlineData("TestCase")]
    [InlineData("TestMethod")]
    [InlineData("DataRow")]
    [InlineData("SetUp")]
    [InlineData("OneTimeSetUp")]
    [InlineData("TestInitialize")]
    [InlineData("ClassCleanup")]
    public void Classify_TestMethodAttribute_ReturnsTestMethod(string attrName)
    {
        var src = $$"""
            class {{attrName}}Attribute : System.Attribute {}
            class C { [{{attrName}}] public void M() {} }
            """;
        var method = GetMethod(src, "C", "M");
        Assert.Equal(DeadCodeFilters.Reason.TestMethod, DeadCodeFilters.Classify(method));
    }

    [Theory]
    [InlineData("TestClass")]
    [InlineData("TestFixture")]
    [InlineData("Collection")]
    public void Classify_TestContainerType_ReturnsTestContainer(string attrName)
    {
        var src = $$"""
            class {{attrName}}Attribute : System.Attribute {}
            [{{attrName}}] class C { public void M() {} }
            """;
        var type = GetType(src, "C");
        Assert.Equal(DeadCodeFilters.Reason.TestContainer, DeadCodeFilters.Classify(type));
    }

    [Fact]
    public void Classify_MethodInTestContainer_ReturnsTestContainer()
    {
        var src = """
            class TestClassAttribute : System.Attribute {}
            [TestClass] class C { public void M() {} }
            """;
        var method = GetMethod(src, "C", "M");
        Assert.Equal(DeadCodeFilters.Reason.TestContainer, DeadCodeFilters.Classify(method));
    }

    [Fact]
    public void Classify_MethodInBaseTestClass_ReturnsTestContainer()
    {
        var src = """
            class TestClassAttribute : System.Attribute {}
            [TestClass] class Base { public void M() {} }
            class Derived : Base {}
            """;
        var method = GetMethod(src, "Base", "M");
        Assert.Equal(DeadCodeFilters.Reason.TestContainer, DeadCodeFilters.Classify(method));
    }

    [Fact]
    public void Classify_MethodInheritsTestContainerViaBaseChain_ReturnsTestContainer()
    {
        var src = """
            class TestClassAttribute : System.Attribute {}
            [TestClass] class Base { public virtual void Helper() {} }
            class Derived : Base { public void OwnHelper() {} }
            """;
        var method = GetMethod(src, "Derived", "OwnHelper");
        Assert.Equal(DeadCodeFilters.Reason.TestContainer, DeadCodeFilters.Classify(method));
    }

    [Theory]
    [InlineData("McpServerTool")]
    [InlineData("McpServerToolType")]
    public void Classify_McpAttribute_ReturnsMcpTool(string attrName)
    {
        var src = $$"""
            class {{attrName}}Attribute : System.Attribute {}
            class C { [{{attrName}}] public void Execute() {} }
            """;
        var method = GetMethod(src, "C", "Execute");
        Assert.Equal(DeadCodeFilters.Reason.McpTool, DeadCodeFilters.Classify(method));
    }

    [Fact]
    public void Classify_McpServerToolTypeOnClass_FiltersClassAndMembers()
    {
        var src = """
            class McpServerToolTypeAttribute : System.Attribute {}
            class McpServerToolAttribute : System.Attribute {}
            [McpServerToolType] class MyTool {
                [McpServerTool] public void Execute() {}
            }
            """;
        var type = GetType(src, "MyTool");
        var method = GetMethod(src, "MyTool", "Execute");
        Assert.Equal(DeadCodeFilters.Reason.McpTool, DeadCodeFilters.Classify(type));
        Assert.Equal(DeadCodeFilters.Reason.McpTool, DeadCodeFilters.Classify(method));
    }

    [Theory]
    [InlineData("CompilerGenerated")]
    [InlineData("GeneratedCode")]
    [InlineData("DebuggerNonUserCode")]
    public void Classify_GeneratedAttribute_ReturnsGenerated(string attrName)
    {
        var src = $$"""
            namespace System.Runtime.CompilerServices { class {{attrName}}Attribute : System.Attribute {} }
            class C { [System.Runtime.CompilerServices.{{attrName}}] public void M() {} }
            """;
        var method = GetMethod(src, "C", "M");
        Assert.Equal(DeadCodeFilters.Reason.Generated, DeadCodeFilters.Classify(method));
    }

    [Theory]
    [InlineData("Export")]
    [InlineData("Import")]
    [InlineData("ImportMany")]
    [InlineData("ImportingConstructor")]
    public void Classify_CompositionAttribute_ReturnsComposition(string attrName)
    {
        var src = $$"""
            class {{attrName}}Attribute : System.Attribute {}
            class C { [{{attrName}}] public void M() {} }
            """;
        var method = GetMethod(src, "C", "M");
        Assert.Equal(DeadCodeFilters.Reason.Composition, DeadCodeFilters.Classify(method));
    }

    [Fact]
    public void Classify_FieldOffsetAttribute_ReturnsInterop()
    {
        var src = """
            namespace System.Runtime.InteropServices {
                class FieldOffsetAttribute : System.Attribute { public FieldOffsetAttribute(int o) {} }
                class StructLayoutAttribute : System.Attribute { public StructLayoutAttribute(int l) {} }
            }
            [System.Runtime.InteropServices.StructLayout(0)]
            struct S { [System.Runtime.InteropServices.FieldOffset(0)] public int X; }
            """;
        var field = GetField(src, "S", "X");
        Assert.Equal(DeadCodeFilters.Reason.Interop, DeadCodeFilters.Classify(field));
    }

    [Fact]
    public void Classify_FieldInStructLayout_ReturnsInterop()
    {
        var src = """
            namespace System.Runtime.InteropServices {
                class StructLayoutAttribute : System.Attribute { public StructLayoutAttribute(int l) {} }
            }
            [System.Runtime.InteropServices.StructLayout(0)]
            struct S { public int Plain; }
            """;
        var field = GetField(src, "S", "Plain");
        Assert.Equal(DeadCodeFilters.Reason.Interop, DeadCodeFilters.Classify(field));
    }

    [Fact]
    public void Classify_AttributeMatchesWithoutSuffix()
    {
        var src = """
            class FactAttribute : System.Attribute {}
            class C { [Fact] public void M() {} }
            """;
        var method = GetMethod(src, "C", "M");
        Assert.Equal(DeadCodeFilters.Reason.TestMethod, DeadCodeFilters.Classify(method));
    }

    [Fact]
    public void Classify_CustomAttributeInheritingFromKnown_ReturnsTestMethod()
    {
        var src = """
            class FactAttribute : System.Attribute {}
            class MyFactAttribute : FactAttribute {}
            class C { [MyFact] public void M() {} }
            """;
        var method = GetMethod(src, "C", "M");
        Assert.Equal(DeadCodeFilters.Reason.TestMethod, DeadCodeFilters.Classify(method));
    }

    // --- Helpers ---

    private static INamedTypeSymbol GetType(string source, string typeName)
    {
        var compilation = Compile(source);
        return compilation.GetTypeByMetadataName(typeName)
            ?? throw new InvalidOperationException($"Type {typeName} not found");
    }

    private static IMethodSymbol GetMethod(string source, string typeName, string methodName)
    {
        var type = GetType(source, typeName);
        return type.GetMembers(methodName).OfType<IMethodSymbol>().First();
    }

    private static IFieldSymbol GetField(string source, string typeName, string fieldName)
    {
        var type = GetType(source, typeName);
        return type.GetMembers(fieldName).OfType<IFieldSymbol>().First();
    }

    private static CSharpCompilation Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();
        return CSharpCompilation.Create("Test", new[] { tree }, refs);
    }
}
