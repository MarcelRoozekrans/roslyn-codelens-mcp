# Dead-Code False-Positive Filters Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add an attribute-based filter step to `find_unused_symbols` so test methods, MCP tool entry points, source-generator output, MEF-composed services, and interop-laid-out fields no longer get falsely flagged as dead code.

**Architecture:** A new `internal static class DeadCodeFilters` exposes a single `Classify(ISymbol) → Reason` function and seven `Reason` enum values. `FindUnusedSymbolsLogic` calls it after its existing accessibility/override skip checks; matched symbols are skipped and counted by reason. `FindUnusedSymbolsLogic.Execute` returns a tuple `(items, filteredCounts)` so the Tool wrapper can extend the envelope's `summary` aggregate with a `filteredOut: { testMethod, testContainer, mcpTool, generated, composition, interop }` block.

**Tech Stack:** C# / .NET 10, Roslyn (Microsoft.CodeAnalysis), xUnit, ModelContextProtocol.Server.

**Design doc:** `docs/plans/2026-05-22-dead-code-filters-design.md`

**Source inspiration:** `sailro/RoslynMcpExtension/src/RoslynMcpExtension/Services/DeadCodeAnalysisService.cs` (MIT — patterns may be referenced; this implementation is clean-room based on the same idea).

---

## Conventions

- TDD: write the failing test, run it, see it fail, then implement.
- Per-task commit: `feat(filters): <task summary>` for code, `test(filters): <summary>` if test-only.
- Scoped test runs: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~<class>"`.
- Full suite before merge: `dotnet test`.
- `_loaded`, `_resolver` etc. on test classes come from the shared `TestSolutionFixture` via `[Collection("TestSolution")]`. Don't break that pattern.
- The known `IsAdapterRestoreFlake` filter in `GetDiagnosticsToolTests` exists because xUnit/NUnit/MSTest fixture restore is environmentally flaky on Windows/Linux CI — preserve it where it appears.

---

## Task 1: Add `DeadCodeFilters` + unit tests

**Files:**
- Create: `src/RoslynCodeLens/Tools/DeadCodeFilters.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/DeadCodeFiltersTests.cs`

**Step 1: Write the failing tests**

Tests use Roslyn's `CSharpCompilation.Create` to spin up tiny inline compilations and pull `ISymbol` instances out of them. Each test feeds a manufactured symbol to `DeadCodeFilters.Classify` and asserts the returned `Reason`.

```csharp
// tests/RoslynCodeLens.Tests/Tools/DeadCodeFiltersTests.cs
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
        // Member on a base class whose subclass has [TestClass] is NOT caught
        // (that's the inverted-inheritance case we decided to defer).
        // But a member on a base WITH [TestClass] IS caught even when accessed via derived.
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
        // [Fact] should match the same as [FactAttribute]
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
        // A user-defined attribute inheriting from FactAttribute is still a test attribute.
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
```

**Step 2: Run tests, verify they fail to compile**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~DeadCodeFiltersTests"`
Expected: compile error — `DeadCodeFilters` does not exist.

**Step 3: Implement `DeadCodeFilters`**

```csharp
// src/RoslynCodeLens/Tools/DeadCodeFilters.cs
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
        // The "container" can be the symbol itself (if it's a type) or any ancestor type.
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type != null; type = type.BaseType)
        {
            if (HasAnyAttribute(type, TestContainerAttributes))
                return true;

            // A type with any test-method-attributed method is also a container.
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
```

**Step 4: Run tests, expect all pass**

`dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~DeadCodeFiltersTests"`

**Step 5: Commit**

```
git add src/RoslynCodeLens/Tools/DeadCodeFilters.cs tests/RoslynCodeLens.Tests/Tools/DeadCodeFiltersTests.cs
git commit -m "feat(filters): add DeadCodeFilters classifier + unit tests"
```

---

## Task 2: Wire `DeadCodeFilters` into `FindUnusedSymbolsLogic`

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindUnusedSymbolsLogic.cs`

The logic's signature changes from `IReadOnlyList<UnusedSymbolInfo> Execute(...)` to a tuple returning the items + filter counts. The Tool wrapper (Task 3) will consume the tuple.

**Step 1: Update logic file**

```csharp
// src/RoslynCodeLens/Tools/FindUnusedSymbolsLogic.cs

// New result type at top of file (or just use ValueTuple inline)
public static (IReadOnlyList<UnusedSymbolInfo> Items, IReadOnlyDictionary<string, int> FilteredCounts) Execute(
    LoadedSolution loaded, SymbolResolver resolver, string? project, bool includeInternal)
{
    var referencedSymbols = CollectReferencedSymbols(loaded);
    var (items, counts) = FindUnusedTypesWithFilterCounts(resolver, referencedSymbols, project, includeInternal);
    return (items, counts);
}

private static (List<UnusedSymbolInfo> items, Dictionary<string, int> counts)
    FindUnusedTypesWithFilterCounts(
        SymbolResolver resolver, HashSet<ISymbol> referencedSymbols, string? project, bool includeInternal)
{
    var results = new List<UnusedSymbolInfo>();
    var counts = NewCounts();

    foreach (var type in resolver.AllTypes)
    {
        if (!type.Locations.Any(l => l.IsInSource)) continue;

        var projectName = resolver.GetProjectName(type);
        if (project != null && !projectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            continue;

        if (ShouldSkipType(type, includeInternal)) continue;

        var typeReason = DeadCodeFilters.Classify(type);
        if (typeReason != DeadCodeFilters.Reason.None)
        {
            counts[KeyFor(typeReason)]++;
            continue;
        }

        if (!referencedSymbols.Contains(type))
        {
            var (file, line) = resolver.GetFileAndLine(type);
            results.Add(new UnusedSymbolInfo(
                type.ToDisplayString(), type.TypeKind.ToString(),
                file, line, projectName, resolver.IsGenerated(file)));
            continue;
        }

        CollectUnusedMembers(type, referencedSymbols, includeInternal, projectName, resolver, results, counts);
    }

    return (results, counts);
}

private static void CollectUnusedMembers(
    INamedTypeSymbol type, HashSet<ISymbol> referencedSymbols, bool includeInternal,
    string projectName, SymbolResolver resolver, List<UnusedSymbolInfo> results,
    Dictionary<string, int> counts)
{
    foreach (var member in type.GetMembers())
    {
        if (ShouldSkipMember(member, type, includeInternal)) continue;

        var memberReason = DeadCodeFilters.Classify(member);
        if (memberReason != DeadCodeFilters.Reason.None)
        {
            counts[KeyFor(memberReason)]++;
            continue;
        }

        if (!referencedSymbols.Contains(member))
        {
            var (file, line) = resolver.GetFileAndLine(member);
            var kind = member switch
            {
                IMethodSymbol => "Method",
                IPropertySymbol => "Property",
                IFieldSymbol => "Field",
                IEventSymbol => "Event",
                _ => member.Kind.ToString(),
            };
            var memberSymbol = $"{type.ToDisplayString()}.{member.Name}";
            results.Add(new UnusedSymbolInfo(
                memberSymbol, kind, file, line, projectName, resolver.IsGenerated(file)));
        }
    }
}

private static Dictionary<string, int> NewCounts() => new(StringComparer.Ordinal)
{
    ["testMethod"] = 0,
    ["testContainer"] = 0,
    ["mcpTool"] = 0,
    ["generated"] = 0,
    ["composition"] = 0,
    ["interop"] = 0,
};

private static string KeyFor(DeadCodeFilters.Reason reason) => reason switch
{
    DeadCodeFilters.Reason.TestMethod => "testMethod",
    DeadCodeFilters.Reason.TestContainer => "testContainer",
    DeadCodeFilters.Reason.McpTool => "mcpTool",
    DeadCodeFilters.Reason.Generated => "generated",
    DeadCodeFilters.Reason.Composition => "composition",
    DeadCodeFilters.Reason.Interop => "interop",
    _ => throw new ArgumentOutOfRangeException(nameof(reason)),
};
```

Keep the existing `ShouldSkipType`, `ShouldSkipMember`, and `CollectReferencedSymbols` unchanged — they handle accessibility / interface / override skips, which are orthogonal to attribute-based filtering.

**Step 2: Build — this will break `FindUnusedSymbolsTool` and its tests because the return type changed**

Run: `dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj`
Expected: compile errors in `FindUnusedSymbolsTool.cs` (it still calls `FindUnusedSymbolsLogic.Execute(...)` expecting `IReadOnlyList<>`).

That's intentional — Task 3 fixes the Tool wrapper.

**Step 3: Commit (build broken — intentional intermediate state)**

```
git add src/RoslynCodeLens/Tools/FindUnusedSymbolsLogic.cs
git commit -m "feat(filters): wire DeadCodeFilters into FindUnusedSymbolsLogic"
```

> Tip: if you prefer all-green commits, fold Task 3 into this one. The split exists for review clarity — you can `git commit --amend` after Task 3 if you want a single atomic commit.

---

## Task 3: Update `FindUnusedSymbolsTool` to surface `filteredOut` summary

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindUnusedSymbolsTool.cs`
- Modify: `tests/RoslynCodeLens.Tests/Tools/FindUnusedSymbolsToolTests.cs`

**Step 1: Add the new envelope test (will fail to compile)**

Append to `FindUnusedSymbolsToolTests.cs`:

```csharp
[Fact]
public void BuildSummary_IncludesFilteredOutWithAllReasonKeys()
{
    var items = new List<UnusedSymbolInfo>
    {
        new("X", "Class", "a.cs", 1, "P"),
    };
    var filteredCounts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["testMethod"] = 5,
        ["testContainer"] = 2,
        ["mcpTool"] = 8,
        ["generated"] = 0,
        ["composition"] = 0,
        ["interop"] = 0,
    };

    var summary = FindUnusedSymbolsTool.BuildSummary(items, filteredCounts);
    var json = System.Text.Json.JsonSerializer.Serialize(summary);

    // byKind unchanged
    Assert.Contains("\"Class\":1", json, StringComparison.Ordinal);
    // filteredOut block with all six keys
    Assert.Contains("\"testMethod\":5", json, StringComparison.Ordinal);
    Assert.Contains("\"testContainer\":2", json, StringComparison.Ordinal);
    Assert.Contains("\"mcpTool\":8", json, StringComparison.Ordinal);
    Assert.Contains("\"generated\":0", json, StringComparison.Ordinal);
    Assert.Contains("\"composition\":0", json, StringComparison.Ordinal);
    Assert.Contains("\"interop\":0", json, StringComparison.Ordinal);
}
```

**Step 2: Update existing `BuildSummary` signature in the test file**

The existing test `BuildSummary_GroupsByKind` will break because `BuildSummary` now takes a second parameter. Update it:

```csharp
[Fact]
public void BuildSummary_GroupsByKind()
{
    var input = new List<UnusedSymbolInfo>
    {
        new("X", "Class",  "a.cs", 1, "P"),
        new("Y", "Class",  "a.cs", 2, "P"),
        new("Z", "Method", "a.cs", 3, "P"),
    };

    var summary = FindUnusedSymbolsTool.BuildSummary(input, EmptyCounts());
    var json = System.Text.Json.JsonSerializer.Serialize(summary);

    Assert.Contains("\"Class\":2", json, StringComparison.Ordinal);
    Assert.Contains("\"Method\":1", json, StringComparison.Ordinal);
}

private static IReadOnlyDictionary<string, int> EmptyCounts()
    => new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["testMethod"] = 0, ["testContainer"] = 0, ["mcpTool"] = 0,
        ["generated"] = 0, ["composition"] = 0, ["interop"] = 0,
    };
```

Also update the existing `FindUnusedSymbols_ReturnsResults` and `FindUnusedSymbols_ProjectFilter_FiltersResults` tests:

```csharp
[Fact]
public void FindUnusedSymbols_ReturnsResults()
{
    var (items, _) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, null, false);
    Assert.NotNull(items);
}

[Fact]
public void FindUnusedSymbols_ProjectFilter_FiltersResults()
{
    var (items, _) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
    Assert.All(items, r => Assert.Contains("TestLib", r.Project, StringComparison.Ordinal));
}
```

**Step 3: Run — expect compile error**

Run: `dotnet build`
Expected: `BuildSummary` overload not found.

**Step 4: Update `FindUnusedSymbolsTool.cs`**

```csharp
// src/RoslynCodeLens/Tools/FindUnusedSymbolsTool.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindUnusedSymbolsTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "find_unused_symbols"),
     Description("Find potentially unused types and members (dead code detection). Checks public symbols for references across the solution. " +
                 "Filters out test methods, MCP tools, source-generator output, MEF-composed services, and interop-laid-out fields. " +
                 "Returns an envelope with items, totalCount, truncated, limit (default 500), and a summary including byKind + filteredOut counts.")]
    public static ToolListResult<UnusedSymbolInfo> Execute(
        MultiSolutionManager manager,
        [Description("Optional project name filter")] string? project = null,
        [Description("Include internal symbols (default: false)")] bool includeInternal = false,
        [Description("Maximum number of items to return (default: 500). Items are sorted by project, then file.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var (raw, filteredCounts) = FindUnusedSymbolsLogic.Execute(
            manager.GetLoadedSolution(), manager.GetResolver(), project, includeInternal);

        var sorted = Sort(raw);
        var summary = BuildSummary(raw, filteredCounts);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    internal static IReadOnlyList<UnusedSymbolInfo> Sort(IReadOnlyList<UnusedSymbolInfo> items)
        => items
            .OrderBy(u => u.Project, StringComparer.Ordinal)
            .ThenBy(u => u.File, StringComparer.Ordinal)
            .ThenBy(u => u.Line)
            .ToList();

    internal static object BuildSummary(
        IReadOnlyList<UnusedSymbolInfo> items,
        IReadOnlyDictionary<string, int> filteredCounts)
    {
        var byKind = items
            .GroupBy(u => u.SymbolKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Reproject filteredCounts into an anonymous shape so the JSON has predictable keys.
        var filteredOut = new
        {
            testMethod = filteredCounts.GetValueOrDefault("testMethod", 0),
            testContainer = filteredCounts.GetValueOrDefault("testContainer", 0),
            mcpTool = filteredCounts.GetValueOrDefault("mcpTool", 0),
            generated = filteredCounts.GetValueOrDefault("generated", 0),
            composition = filteredCounts.GetValueOrDefault("composition", 0),
            interop = filteredCounts.GetValueOrDefault("interop", 0),
        };

        return new { byKind, filteredOut };
    }
}
```

**Step 5: Build + run tests**

`dotnet build && dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~FindUnusedSymbolsToolTests"`

Expected: all green.

**Step 6: Commit**

```
git add src/RoslynCodeLens/Tools/FindUnusedSymbolsTool.cs tests/RoslynCodeLens.Tests/Tools/FindUnusedSymbolsToolTests.cs
git commit -m "feat(filters): surface filteredOut in find_unused_symbols summary"
```

---

## Task 4: Add fixture files for Generated / MEF / Interop categories

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/FilterFixtures.cs`

The existing `XUnitFixture/NUnitFixture/MSTestFixture` projects already exercise the test-attribute filters. We need new fixture content for Generated / MEF / Interop. To minimize new csproj churn, append these all to the existing `TestLib` project as a single file.

```csharp
// tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/FilterFixtures.cs
using System;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TestLib.FilterFixtures;

// Generated-code filter targets ----------------------------------------------

[CompilerGenerated]
public class GeneratedClass
{
    public void NeverReferenced() { }
}

public class HostClass
{
    [GeneratedCode("MyGen", "1.0")]
    public void GeneratedMember() { }
}

// MEF composition filter targets ---------------------------------------------

[Export]
public class ExportedService
{
    [ImportingConstructor]
    public ExportedService() { }
}

public class ImportHost
{
    [Import] public ExportedService? Service { get; set; }
}

// Interop filter targets -----------------------------------------------------

[StructLayout(LayoutKind.Explicit)]
public struct InteropStruct
{
    [FieldOffset(0)] public int Header;
    public int PlainFieldInLaidOutStruct;
}
```

**Step 1: Check whether `TestLib.csproj` already references `System.ComponentModel.Composition`**

```
grep -i "Composition" tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/TestLib.csproj
```

If not present, add it. The `System.ComponentModel.Composition` attributes (`ExportAttribute`, `ImportAttribute`, `ImportingConstructorAttribute`) are not in BCL by default in modern .NET — they're in a NuGet package. If adding the package risks restore flake (we already have a documented flake for adapter projects), instead **inline minimal attribute definitions** at the top of `FilterFixtures.cs`:

```csharp
// At top of FilterFixtures.cs, before the namespace, only if the System.ComponentModel.Composition
// NuGet package can't be added safely:
namespace System.ComponentModel.Composition
{
    [AttributeUsage(AttributeTargets.Class | AttributeUsage.Method | AttributeTargets.Property | AttributeTargets.Field)]
    internal class ExportAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal class ImportAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Constructor)]
    internal class ImportingConstructorAttribute : Attribute { }
}
```

DeadCodeFilters matches by **simple name** so inline shims work fine — the production behavior on real MEF projects is identical because `Export`/`Import` simple names match regardless of namespace.

**Step 2: Build the fixture solution**

`dotnet build tests/RoslynCodeLens.Tests` should still succeed (or if it doesn't, fix the references).

**Step 3: Commit**

```
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/FilterFixtures.cs
git commit -m "test(filters): add Generated/MEF/Interop fixture content to TestLib"
```

---

## Task 5: Integration tests against the fixture

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/FindUnusedSymbolsToolTests.cs`

**Step 1: Append integration tests**

```csharp
[Fact]
public void FilteredOut_IncludesXUnitFixtureTestMethods()
{
    // XUnitFixture has [Fact]-annotated methods — none should appear in items;
    // they should be counted under filteredOut.testMethod.
    var (items, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "XUnitFixture", false);

    // No XUnitFixture test methods in the unused list
    var fixtureItems = items.Where(i => i.Project == "XUnitFixture").ToList();
    var hasTestMethodNamedSample = fixtureItems.Any(i => i.SymbolName.Contains("Test", StringComparison.Ordinal));
    Assert.False(hasTestMethodNamedSample,
        $"XUnitFixture test methods leaked into unused list: {string.Join(",", fixtureItems.Select(i => i.SymbolName))}");

    // At least one filter category should have a nonzero count (precise count is brittle)
    var totalFiltered = counts["testMethod"] + counts["testContainer"];
    Assert.True(totalFiltered > 0, $"Expected test-related filtering, got counts={string.Join(",", counts.Select(kv => $"{kv.Key}={kv.Value}"))}");
}

[Fact]
public void FilteredOut_FixtureProject_HasGeneratedCount()
{
    var (_, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
    Assert.True(counts["generated"] >= 1, "Expected GeneratedClass / GeneratedMember to be filtered");
}

[Fact]
public void FilteredOut_FixtureProject_HasCompositionCount()
{
    var (_, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
    Assert.True(counts["composition"] >= 1, "Expected ExportedService / ImportHost to be filtered");
}

[Fact]
public void FilteredOut_FixtureProject_HasInteropCount()
{
    var (_, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
    Assert.True(counts["interop"] >= 1, "Expected InteropStruct fields to be filtered");
}

[Fact]
public void GeneratedClass_DoesNotAppearAsUnused()
{
    var (items, _) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);
    Assert.DoesNotContain(items,
        i => i.SymbolName.Contains("GeneratedClass", StringComparison.Ordinal));
}
```

**Step 2: Run**

`dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~FindUnusedSymbolsToolTests"`
Expected: all green.

**Step 3: Commit**

```
git add tests/RoslynCodeLens.Tests/Tools/FindUnusedSymbolsToolTests.cs
git commit -m "test(filters): verify fixtures are filtered with correct reason"
```

---

## Task 6: MCP self-test (dogfooding gate)

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/Tools/FindUnusedSymbolsToolTests.cs`

This is the most important test in the plan. It exercises the MCP-attribute filter against the most realistic scenario possible: synthetic types in the fixture solution that mimic the structure of our production `*Tool.cs` files.

**Step 1: Add MCP attribute shims and a synthetic tool to `FilterFixtures.cs`**

Append to `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/FilterFixtures.cs`:

```csharp
// MCP attribute shims — match by simple name regardless of namespace.
namespace ModelContextProtocol.Server
{
    [AttributeUsage(AttributeTargets.Class)]
    internal class McpServerToolTypeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    internal class McpServerToolAttribute : Attribute
    {
        public string? Name { get; set; }
    }
}

namespace TestLib.FilterFixtures
{
    using ModelContextProtocol.Server;

    [McpServerToolType]
    public static class SyntheticMcpTool
    {
        [McpServerTool(Name = "synthetic")]
        public static string Execute() => "ok";
    }
}
```

**Step 2: Add the dogfooding test**

```csharp
[Fact]
public void McpToolExecuteMethods_NeverFlaggedUnused()
{
    var (items, counts) = FindUnusedSymbolsLogic.Execute(_loaded, _resolver, "TestLib", false);

    Assert.DoesNotContain(items,
        i => i.SymbolName.Contains("SyntheticMcpTool", StringComparison.Ordinal));
    Assert.DoesNotContain(items,
        i => i.SymbolName.EndsWith(".Execute", StringComparison.Ordinal)
          && i.SymbolName.Contains("Synthetic", StringComparison.Ordinal));

    Assert.True(counts["mcpTool"] >= 1, "Expected SyntheticMcpTool to be filtered");
}
```

**Step 3: Run scoped tests + full suite**

```
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~FindUnusedSymbolsToolTests"
dotnet test
```

Expected: green, except the known `IsAdapterRestoreFlake`-class environmental flakes documented in earlier work (`GetDiagnostics_CleanSolution_ReturnsNoErrors`, the test-framework discovery tests). Those are pre-existing and unrelated.

**Step 4: Commit**

```
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/FilterFixtures.cs tests/RoslynCodeLens.Tests/Tools/FindUnusedSymbolsToolTests.cs
git commit -m "test(filters): MCP self-test — Tool.Execute methods never flagged"
```

---

## Task 7: Manual smoke + final verification

**Step 1: Full build + test**

```
dotnet build
dotnet test
```

Both must succeed (modulo the known environmental flake).

**Step 2: Local smoke test via MCP server**

Start the MCP server from the project root using the existing `.mcp.json`:
- Restart Claude Code so it reloads the MCP server.
- Call `find_unused_symbols` (no args).
- Confirm in the response:
  - `items` does NOT contain any `*Tool.Execute` from `RoslynCodeLens.Tools.*`
  - `items` does NOT contain test methods from the fixture projects
  - `summary.filteredOut.mcpTool > 0` (should be ≥ 50 given our tool count)
  - `summary.filteredOut.testMethod > 0`

If any production tool's `Execute` appears: bug, do not merge.

**Step 3: Update SKILL.md tool description**

`plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` references `find_unused_symbols`. Verify the description still reads accurately given the new filtering behavior — likely just add a short line under the existing dead-code discussion: "Filters out test methods, MCP tools, source-generator output, MEF composition, and interop fields."

**Step 4: Commit any SKILL.md tweak**

```
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md
git commit -m "docs(skill): note dead-code filter coverage in find_unused_symbols"
```

**Step 5: Push + PR**

Branch is `feat/dead-code-filters`. Push, open PR titled "feat: filter false positives from find_unused_symbols (tests, MCP tools, generators, MEF, interop)" with body summarizing the design.

---

## Gotchas

- **MCP attribute matching is by simple name.** Our production `[McpServerTool]` is `ModelContextProtocol.Server.McpServerToolAttribute`. The fixture's `[McpServerTool]` is `TestLib.FilterFixtures.McpServerToolAttribute`. Both match because `HasAnyAttribute` checks the simple class name `McpServerToolAttribute` — namespace-agnostic. **Don't break this** by tightening the matcher to require full namespace.
- **Inline attribute shims are intentional.** They avoid pulling NuGet packages into fixtures (which historically flakes on CI restore — see `IsAdapterRestoreFlake`).
- **`FindUnusedSymbolsLogic.Execute` return type changed** — anything in the codebase that calls it must be updated. As of the design's writing, only `FindUnusedSymbolsTool.cs` calls it. If grep finds others, update them in Task 3.
- **Don't add new csproj projects.** Bundle all fixture content into existing `TestLib`.
- **The `IsInTestContainer` walk-loop** scans every member of every type in the BaseType chain. For pathological deep hierarchies this is O(depth × members). Acceptable for realistic codebases; if profiling later shows it's hot, cache by `INamedTypeSymbol`.
