using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        Benchmark,
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

    // BenchmarkDotNet, namespace BenchmarkDotNet.Attributes
    private static readonly string[] BenchmarkAttributes =
    [
        "Benchmark", "GlobalSetup", "GlobalCleanup", "IterationSetup", "IterationCleanup",
        "Params", "ParamsSource", "ParamsAllValues", "Arguments", "ArgumentsSource",
        "BenchmarkCategory",
    ];

    private static readonly string[] BenchmarkMethodAttributes =
    [
        "Benchmark",
    ];

    private static readonly string[] BenchmarkSourceAttributeNames =
    [
        "ArgumentsSource", "ArgumentsSourceAttribute", "ParamsSource", "ParamsSourceAttribute",
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

        // BenchmarkDotNet members. A benchmark class itself is handled by IsBenchmarkType, so
        // that its members are still walked and a dead helper in it is still reported.
        if (symbol is not INamedTypeSymbol && HasAnyAttribute(symbol, BenchmarkAttributes))
            return Reason.Benchmark;

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

    /// <summary>
    /// True when the type, or a base type, declares a <c>[Benchmark]</c> method. BenchmarkDotNet
    /// finds such a class by reflection, so nothing has to reference it.
    /// </summary>
    public static bool IsBenchmarkType(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IMethodSymbol m && HasAnyAttribute(m, BenchmarkMethodAttributes))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Adds the members an <c>[ArgumentsSource]</c> or <c>[ParamsSource]</c> attribute names to
    /// <paramref name="into"/>. BenchmarkDotNet reads them by name, from the attributed member's
    /// type or from the type given by a leading <c>typeof</c> argument. The name is a constant,
    /// so a string literal and a <c>nameof</c> both resolve. This is needed for <c>nameof</c>
    /// too: <c>nameof</c> of a method names a method group, which binds to no single symbol.
    /// </summary>
    public static void AddBenchmarkSourceMembers(
        AttributeSyntax attribute, SemanticModel model, HashSet<ISymbol> into)
    {
        if (!IsBenchmarkSourceAttributeName(attribute.Name)) return;

        if (attribute.ArgumentList is not { Arguments: { Count: > 0 } args }) return;

        if (model.GetConstantValue(args[args.Count - 1].Expression).Value is not string name) return;

        var target = args[0].Expression is TypeOfExpressionSyntax typeOf
            ? model.GetTypeInfo(typeOf.Type).Type as INamedTypeSymbol
            : FindContainingType(attribute, model);

        for (var type = target; type != null; type = type.BaseType)
        {
            var members = type.GetMembers(name);
            if (members.IsEmpty) continue;

            foreach (var member in members)
            {
                into.Add(member);
                into.Add(member.OriginalDefinition);
            }
            return;
        }
    }

    private static bool IsBenchmarkSourceAttributeName(NameSyntax name)
    {
        var simple = name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => null,
        };
        return simple != null && Array.IndexOf(BenchmarkSourceAttributeNames, simple) >= 0;
    }

    private static INamedTypeSymbol? FindContainingType(AttributeSyntax attribute, SemanticModel model)
    {
        var declaration = attribute.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>();
        return declaration == null ? null : model.GetDeclaredSymbol(declaration) as INamedTypeSymbol;
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
