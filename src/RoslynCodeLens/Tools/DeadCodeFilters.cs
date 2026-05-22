using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Tools;

internal static class DeadCodeFilters
{
    public enum Reason
    {
        None,
        TestMethod,
        TestContainer,
        McpTool,
        Generated,
        Composition,
        Interop,
    }

    private static readonly string[] TestMethodAttributes =
    [
        // xUnit
        "Fact", "Theory", "InlineData", "MemberData", "ClassData",
        // NUnit
        "Test", "TestCase", "TestCaseSource", "Values", "ValueSource", "Range",
        "Random", "Combinatorial", "Pairwise", "Sequential",
        "Datapoint", "DatapointSource",
        "SetUp", "TearDown", "OneTimeSetUp", "OneTimeTearDown",
        // MSTest
        "TestMethod", "DataTestMethod", "DataRow", "DynamicData",
        "TestInitialize", "TestCleanup",
        "ClassInitialize", "ClassCleanup",
        "AssemblyInitialize", "AssemblyCleanup",
    ];

    private static readonly string[] TestContainerAttributes =
    [
        "TestClass", "TestFixture", "TestFixtureSource",
        "Collection", "CollectionDefinition",
    ];

    private static readonly string[] McpAttributes =
    [
        "McpServerTool", "McpServerToolType",
    ];

    private static readonly string[] GeneratedAttributes =
    [
        "CompilerGenerated", "GeneratedCode", "DebuggerNonUserCode",
    ];

    private static readonly string[] CompositionAttributes =
    [
        "Export", "InheritedExport", "Import", "ImportMany", "ImportingConstructor",
    ];

    private static readonly string[] InteropFieldAttributes =
    [
        "FieldOffset", "MarshalAs",
    ];

    private static readonly string[] InteropStructAttributes =
    [
        "StructLayout", "InlineArray",
    ];

    public static Reason Classify(ISymbol symbol)
    {
        // MCP tools — check symbol itself and its containing type
        if (HasAnyAttribute(symbol, McpAttributes)) return Reason.McpTool;
        if (symbol.ContainingType != null && HasAnyAttribute(symbol.ContainingType, McpAttributes))
            return Reason.McpTool;

        // Generated code — check symbol and containing type
        if (HasAnyAttribute(symbol, GeneratedAttributes)) return Reason.Generated;
        if (symbol.ContainingType != null && HasAnyAttribute(symbol.ContainingType, GeneratedAttributes))
            return Reason.Generated;

        // Test method
        if (symbol is IMethodSymbol && HasAnyAttribute(symbol, TestMethodAttributes))
            return Reason.TestMethod;

        // Test container — walk BaseType chain
        if (IsInTestContainer(symbol)) return Reason.TestContainer;

        // Composition (MEF)
        if (HasAnyAttribute(symbol, CompositionAttributes)) return Reason.Composition;
        if (symbol.ContainingType != null && HasAnyAttribute(symbol.ContainingType, CompositionAttributes))
            return Reason.Composition;

        // Interop (fields only)
        if (symbol is IFieldSymbol field)
        {
            if (HasAnyAttribute(field, InteropFieldAttributes)) return Reason.Interop;
            if (field.ContainingType != null && HasAnyAttribute(field.ContainingType, InteropStructAttributes))
                return Reason.Interop;
        }

        return Reason.None;
    }

    private static bool IsInTestContainer(ISymbol symbol)
    {
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type != null; type = type.BaseType)
        {
            if (HasAnyAttribute(type, TestContainerAttributes))
                return true;

            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol m && HasAnyAttribute(m, TestMethodAttributes))
                    return true;
            }
        }
        return false;
    }

    private static bool HasAnyAttribute(ISymbol symbol, string[] names)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            for (var cls = attr.AttributeClass; cls != null; cls = cls.BaseType)
            {
                var simple = cls.Name;
                var simpleNoSuffix = simple.EndsWith("Attribute", StringComparison.Ordinal)
                    ? simple[..^"Attribute".Length]
                    : simple;
                foreach (var name in names)
                {
                    if (string.Equals(simple, name, StringComparison.Ordinal)) return true;
                    if (string.Equals(simple, name + "Attribute", StringComparison.Ordinal)) return true;
                    if (string.Equals(simpleNoSuffix, name, StringComparison.Ordinal)) return true;
                }
            }
        }
        return false;
    }
}
