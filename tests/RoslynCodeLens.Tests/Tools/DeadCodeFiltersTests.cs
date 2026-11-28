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

    [Theory]
    [InlineData("Benchmark")]
    [InlineData("GlobalSetup")]
    [InlineData("GlobalCleanup")]
    [InlineData("IterationSetup")]
    [InlineData("IterationCleanup")]
    [InlineData("Arguments")]
    [InlineData("ArgumentsSource")]
    [InlineData("BenchmarkCategory")]
    public void Classify_BenchmarkMethodAttribute_ReturnsBenchmark(string attrName)
    {
        var src = $$"""
            namespace BenchmarkDotNet.Attributes { class {{attrName}}Attribute : System.Attribute {} }
            class C { [BenchmarkDotNet.Attributes.{{attrName}}] public void M() {} }
            """;
        var method = GetMethod(src, "C", "M");
        Assert.Equal(DeadCodeFilters.Reason.Benchmark, DeadCodeFilters.Classify(method));
    }

    [Theory]
    [InlineData("Params")]
    [InlineData("ParamsSource")]
    [InlineData("ParamsAllValues")]
    public void Classify_BenchmarkParameterAttribute_ReturnsBenchmark(string attrName)
    {
        var src = $$"""
            namespace BenchmarkDotNet.Attributes { class {{attrName}}Attribute : System.Attribute {} }
            class C { [BenchmarkDotNet.Attributes.{{attrName}}] public int P { get; set; } }
            """;
        var property = GetType(src, "C").GetMembers("P").Single();
        Assert.Equal(DeadCodeFilters.Reason.Benchmark, DeadCodeFilters.Classify(property));
    }

    [Fact]
    public void Classify_BenchmarkCategoryOnClass_LeavesTheClassToIsBenchmarkType()
    {
        var src = """
            class BenchmarkCategoryAttribute : System.Attribute {}
            [BenchmarkCategory] class C { public void M() {} }
            """;
        Assert.Equal(DeadCodeFilters.Reason.None, DeadCodeFilters.Classify(GetType(src, "C")));
        Assert.Equal(DeadCodeFilters.Reason.None, DeadCodeFilters.Classify(GetMethod(src, "C", "M")));
    }

    [Fact]
    public void IsBenchmarkType_TypeWithBenchmarkMethodOrDerivedFromOne_ReturnsTrue()
    {
        var src = """
            class BenchmarkAttribute : System.Attribute {}
            abstract class BaseBenchmarks { [Benchmark] public void Run() {} }
            class DerivedBenchmarks : BaseBenchmarks {}
            class Plain { public void M() {} }
            """;
        Assert.True(DeadCodeFilters.IsBenchmarkType(GetType(src, "BaseBenchmarks")));
        Assert.True(DeadCodeFilters.IsBenchmarkType(GetType(src, "DerivedBenchmarks")));
        Assert.False(DeadCodeFilters.IsBenchmarkType(GetType(src, "Plain")));
    }

    [Theory]
    [InlineData("[ArgumentsSource(\"Data\")]", "C.Data")]
    [InlineData("[ArgumentsSource(nameof(Data))]", "C.Data")]
    [InlineData("[ArgumentsSourceAttribute(\"Data\")]", "C.Data")]
    [InlineData("[BenchmarkDotNet.Attributes.ArgumentsSource(\"Data\")]", "C.Data")]
    [InlineData("[global::BenchmarkDotNet.Attributes.ArgumentsSource(\"Data\")]", "C.Data")]
    [InlineData("[ArgumentsSource(DataName)]", "C.Data")]
    [InlineData("[ArgumentsSource(\"Inherited\")]", "Base.Inherited")]
    [InlineData("[ArgumentsSource(typeof(Other), \"Values\")]", "Other.Values")]
    public void AddBenchmarkSourceMembers_ResolvesTheNamedMember(string attribute, string expected)
    {
        var referenced = CollectSourceMembers(attribute);
        Assert.Equal([expected], referenced);
    }

    [Theory]
    [InlineData("[ArgumentsSource(\"Missing\")]")]
    [InlineData("[ArgumentsSource]")]
    [InlineData("[ArgumentsSource(NotConstant)]")]
    [InlineData("[Arguments(\"Data\")]")]
    public void AddBenchmarkSourceMembers_NamesNoMember_AddsNothing(string attribute)
    {
        Assert.Empty(CollectSourceMembers(attribute));
    }

    // --- Helpers ---

    private static List<string> CollectSourceMembers(string attribute)
    {
        var src = $$"""
            namespace BenchmarkDotNet.Attributes
            {
                class ArgumentsSourceAttribute : System.Attribute
                {
                    public ArgumentsSourceAttribute() {}
                    public ArgumentsSourceAttribute(string name) {}
                    public ArgumentsSourceAttribute(System.Type type, string name) {}
                }
                class ArgumentsAttribute : System.Attribute { public ArgumentsAttribute(params object[] values) {} }
            }
            namespace N
            {
                using BenchmarkDotNet.Attributes;
                class Base { public int[] Inherited() => new int[0]; }
                class Other { public static int[] Values() => new int[0]; }
                class C : Base
                {
                    const string DataName = "Data";
                    public static string NotConstant = "Data";
                    public int[] Data() => new int[0];
                    {{attribute}} public void M(int value) {}
                }
            }
            """;
        var compilation = Compile(src);
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var into = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var node in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax>())
            DeadCodeFilters.AddBenchmarkSourceMembers(node, model, into);

        return into
            .Select(s => $"{s.ContainingType.Name}.{s.Name}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

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
