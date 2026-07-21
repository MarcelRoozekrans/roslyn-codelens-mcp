# get_instantiation_options Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `get_instantiation_options` MCP tool answering "how do I construct this type" — constructors, solution-wide static factories, and DI registrations — and extend the shared DI scan both it and `get_di_registrations` use.

**Architecture:** A pure logic class (`GetInstantiationOptionsLogic`) composes three independent discoveries: constructor filtering off `INamedTypeSymbol.InstanceConstructors`, a solution-wide factory scan via `SolutionScanner`, and DI registrations from a new shared `DiRegistrationScanner` extracted out of `GetDiRegistrationsLogic`. A thin `[McpServerToolType]` wrapper adapts it to MCP.

**Tech Stack:** C# 14 / net10.0, Microsoft.CodeAnalysis 5.6, xUnit, `ModelContextProtocol.Server`.

**Design:** `docs/plans/2026-07-21-get-instantiation-options-design.md` — read it first. Its probe-findings table is the source of truth for every non-obvious filter below; do not "simplify" those filters away.

---

## Conventions you must follow

- **Logic vs tool split.** All behaviour lives in `src/RoslynCodeLens/Tools/<Name>Logic.cs` as a pure static class taking `LoadedSolution`/`SymbolResolver`. The `<Name>Tool.cs` wrapper only calls `manager.EnsureLoaded()`, `manager.GetAnalysisContext()`, and the logic. Tests target the logic.
- **Errors** are `throw new McpToolException(ToolErrorCode.SymbolNotFound, "message", new { symbol })`. Never return an error object.
- **Solution-wide scans** go through `SolutionScanner.EnumerateTrees` — never hand-roll `foreach (compilation) foreach (tree)`. Read its XML docs before use.
- **Cross-compilation symbol matching uses fully-qualified display strings**, never `SymbolEqualityComparer` (false negatives across compilations).
- **Test fixture:** `RenameTestWorkspace.Create(...)` returns `(LoadedSolution, SymbolResolver)`. A project's assembly name equals its project name — that is how `InternalsVisibleTo` is tested.
- Run one test: `dotnet test --filter "FullyQualifiedName~<TestName>"` from the worktree root.
- Commit after each task.

---

### Task 1: Result models

**Files:**
- Create: `src/RoslynCodeLens/Models/InstantiationOptions.cs`

**Step 1: Write the models**

```csharp
namespace RoslynCodeLens.Models;

/// <param name="Accessible">
/// Null when no caller context was supplied — meaning "not computed", NOT "inaccessible".
/// </param>
public record ConstructorOption(
    string Signature,
    string Accessibility,
    bool? Accessible,
    bool IsImplicit,
    bool IsObsolete,
    IReadOnlyList<ParameterOption> Parameters,
    string? File,
    int? Line);

public record ParameterOption(string Type, string Name, bool HasDefault);

/// <param name="Kind">`method`, `property`, or `field`.</param>
/// <param name="IsAsync">Return type was Task&lt;T&gt;/ValueTask&lt;T&gt; and has been unwrapped.</param>
public record FactoryOption(
    string Signature,
    string DeclaringType,
    string Kind,
    string Accessibility,
    bool? Accessible,
    bool IsAsync,
    bool IsObsolete,
    IReadOnlyList<ParameterOption> Parameters,
    string? File,
    int? Line);

public record RequiredMemberOption(string Type, string Name);

/// <param name="Instantiable">
/// False for interfaces, static classes, and abstract classes. When false, `Constructors` is empty
/// even though Roslyn still exposes constructors for abstract types, and `Note` says why.
/// </param>
public record InstantiationOptionsResult(
    string Type,
    string TypeKind,
    bool Instantiable,
    string? Note,
    IReadOnlyList<ConstructorOption> Constructors,
    IReadOnlyList<FactoryOption> Factories,
    IReadOnlyList<DiRegistration> DiRegistrations,
    IReadOnlyList<RequiredMemberOption> RequiredMembers);
```

**Step 2: Build**

Run: `dotnet build`
Expected: clean.

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/Models/InstantiationOptions.cs
git commit -m "feat(instantiation): result models"
```

---

### Task 2: Constructor discovery

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetInstantiationOptionsLogic.cs`
- Test: `tests/RoslynCodeLens.Tests/GetInstantiationOptionsLogicTests.cs`

**Step 1: Write the failing tests**

These four cases are the probe findings. Each one fails differently if the filter is wrong.

```csharp
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;
using Xunit;

namespace RoslynCodeLens.Tests;

public class GetInstantiationOptionsLogicTests
{
    private const string Source = """
        namespace Demo;

        public class Plain { public Plain() {} public Plain(int a) {} private Plain(bool b) {} }
        public record Rec(int A, string B);
        public struct S { public int X; }
        public abstract class Abs { protected Abs() {} }
        public static class Stat { }
        public interface IFoo { }
        public class Implicit { }
        """;

    private static InstantiationOptionsResult Run(string symbol, string? fromProject = null)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Demo.cs", Source));
        return GetInstantiationOptionsLogic.Execute(loaded, resolver, symbol, fromProject);
    }

    [Fact]
    public void Reports_all_declared_constructors_with_accessibility()
    {
        var r = Run("Plain");
        Assert.True(r.Instantiable);
        Assert.Equal(3, r.Constructors.Count);
        Assert.Contains(r.Constructors, c => c.Accessibility == "private");
    }

    [Fact]
    public void Record_implicit_copy_constructor_is_excluded()
    {
        var r = Run("Rec");
        // Roslyn exposes a protected implicit Rec(Rec); it is never a construction option.
        Assert.DoesNotContain(r.Constructors, c => c.Parameters.Count == 1 && c.Parameters[0].Type.Contains("Rec"));
        Assert.Contains(r.Constructors, c => c.Parameters.Count == 2);
    }

    [Fact]
    public void Struct_implicit_parameterless_constructor_is_reported()
    {
        var r = Run("S");
        var ctor = Assert.Single(r.Constructors);
        Assert.Empty(ctor.Parameters);
        Assert.True(ctor.IsImplicit);
    }

    [Fact]
    public void Class_with_no_declared_constructor_reports_implicit_one()
    {
        var r = Run("Implicit");
        Assert.True(Assert.Single(r.Constructors).IsImplicit);
    }

    [Theory]
    [InlineData("Abs")]
    [InlineData("Stat")]
    [InlineData("IFoo")]
    public void Non_instantiable_types_report_no_constructors_and_a_note(string type)
    {
        var r = Run(type);
        Assert.False(r.Instantiable);
        Assert.Empty(r.Constructors);
        Assert.False(string.IsNullOrWhiteSpace(r.Note));
    }

    [Fact]
    public void Abstract_note_points_at_find_implementations()
    {
        Assert.Contains("find_implementations", Run("Abs").Note!);
    }

    [Fact]
    public void Unknown_symbol_throws_SymbolNotFound()
    {
        var ex = Assert.Throws<McpToolException>(() => Run("Nope"));
        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }
}
```

**Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~GetInstantiationOptionsLogicTests"`
Expected: FAIL — `GetInstantiationOptionsLogic` does not exist.

**Step 3: Implement constructor discovery**

Resolve the type via the same path other tools use (`resolver`), then:

```csharp
private static bool IsCopyConstructor(IMethodSymbol c, INamedTypeSymbol type)
    => c.IsImplicitlyDeclared
       && c.Parameters.Length == 1
       && SymbolEqualityComparer.Default.Equals(c.Parameters[0].Type, type);
```

Instantiability:

```csharp
var instantiable = type.TypeKind is not (TypeKind.Interface) && !type.IsAbstract && !type.IsStatic;
var note = type.TypeKind switch
{
    TypeKind.Interface => "Interfaces cannot be constructed directly — use find_implementations to find concrete types.",
    _ when type.IsStatic => "Static classes cannot be instantiated.",
    _ when type.IsAbstract => "Abstract classes cannot be constructed directly — use find_implementations to find concrete subclasses.",
    _ => null
};
```

When `!instantiable`, return an empty constructor list — **do not** list the `protected` constructors Roslyn still exposes on abstract types.

Otherwise map `type.InstanceConstructors`, excluding copy constructors, keeping implicit ones with `IsImplicit = c.IsImplicitlyDeclared`. Accessibility strings are lowercase (`public`, `internal`, `protected`, `private`, `protected internal`, `private protected`). Sort by parameter count ascending, then by signature ordinal.

Obsolete detection:

```csharp
private static bool IsObsolete(ISymbol s)
    => s.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute");
```

File/line come from `s.Locations.FirstOrDefault(l => l.IsInSource)`; null for implicit and metadata symbols.

**Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~GetInstantiationOptionsLogicTests"`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetInstantiationOptionsLogic.cs tests/RoslynCodeLens.Tests/GetInstantiationOptionsLogicTests.cs
git commit -m "feat(instantiation): constructor discovery with record/struct/abstract rules"
```

---

### Task 3: Required members

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetInstantiationOptionsLogic.cs`
- Test: `tests/RoslynCodeLens.Tests/GetInstantiationOptionsLogicTests.cs`

**Step 1: Failing test**

```csharp
[Fact]
public void Required_members_are_reported()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("R.cs", """
        namespace Demo;
        public class Req { public required int A { get; init; } public string B { get; init; } public int C { get; set; } }
        """));
    var r = GetInstantiationOptionsLogic.Execute(loaded, resolver, "Req", null);
    var m = Assert.Single(r.RequiredMembers);
    Assert.Equal("A", m.Name);
}
```

**Step 2:** Run — FAIL (empty list).

**Step 3:** Collect `type.GetMembers()` where `IPropertySymbol { IsRequired: true }` or `IFieldSymbol { IsRequired: true }`. Walk base types too — a required member on a base still must be set. Skip `IsImplicitlyDeclared`.

**Step 4:** Run — PASS.

**Step 5:** Commit `feat(instantiation): report required members`.

---

### Task 4: Solution-wide factory discovery

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetInstantiationOptionsLogic.cs`
- Test: `tests/RoslynCodeLens.Tests/GetInstantiationOptionsLogicTests.cs`

**Step 1: Failing tests**

```csharp
private const string FactorySource = """
    using System.Threading.Tasks;
    namespace Demo;

    public class Widget { internal Widget() {} }
    public static class WidgetFactory { public static Widget Create() => new(); }
    public class WidgetBuilder { public Widget Build() => new(); }

    public class FactoryOnly
    {
        private FactoryOnly() {}
        public static FactoryOnly Create() => new();
        public static Task<FactoryOnly> CreateAsync() => Task.FromResult(new FactoryOnly());
        public static FactoryOnly Instance { get; } = new();
        public static readonly FactoryOnly Default = new();
        public static int NotAFactory() => 1;
    }
    """;

[Fact]
public void Finds_static_factory_declared_on_another_type()
{
    var r = RunFactories("Widget");
    Assert.Contains(r.Factories, f => f.DeclaringType.EndsWith("WidgetFactory") && f.Signature.Contains("Create"));
}

[Fact]
public void Instance_builder_methods_are_excluded()
{
    // WidgetBuilder.Build() returns Widget but is an instance method: the builder itself
    // would need constructing, so it is deliberately not a construction option.
    Assert.DoesNotContain(RunFactories("Widget").Factories, f => f.Signature.Contains("Build"));
}

[Fact]
public void Compiler_generated_backing_field_is_not_a_factory()
{
    // `static FactoryOnly Instance { get; }` emits a static field <Instance>k__BackingField
    // of self type. Reporting it would be a construction option that cannot be written.
    Assert.DoesNotContain(RunFactories("FactoryOnly").Factories, f => f.Signature.Contains("k__BackingField"));
}

[Fact]
public void Static_property_and_field_factories_are_reported()
{
    var f = RunFactories("FactoryOnly").Factories;
    Assert.Contains(f, x => x.Kind == "property" && x.Signature.Contains("Instance"));
    Assert.Contains(f, x => x.Kind == "field" && x.Signature.Contains("Default"));
}

[Fact]
public void Task_returning_factory_is_unwrapped_and_marked_async()
{
    var f = Assert.Single(RunFactories("FactoryOnly").Factories, x => x.Signature.Contains("CreateAsync"));
    Assert.True(f.IsAsync);
}

[Fact]
public void Members_not_returning_the_type_are_excluded()
{
    Assert.DoesNotContain(RunFactories("FactoryOnly").Factories, x => x.Signature.Contains("NotAFactory"));
}
```

**Step 2:** Run — FAIL (`Factories` empty).

**Step 3: Implement**

Scan with `SolutionScanner.EnumerateTrees`. For each tree, get the semantic model **only if** the tree declares any type (cheap syntactic pre-filter), then walk declared types and their static members.

Matching rule — compare **fully-qualified display strings**, not symbols:

```csharp
var targetKey = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

static ITypeSymbol Unwrap(ITypeSymbol t, out bool isAsync)
{
    isAsync = false;
    if (t is INamedTypeSymbol { IsGenericType: true } g)
    {
        var def = g.ConstructedFrom.ToDisplayString();
        if (def.StartsWith("System.Threading.Tasks.Task<") || def.StartsWith("System.Threading.Tasks.ValueTask<"))
        {
            isAsync = true;
            return g.TypeArguments[0];
        }
    }
    return t;
}
```

Include: static `IMethodSymbol` with `MethodKind.Ordinary`, static `IPropertySymbol` with a getter, static `IFieldSymbol`.
Exclude: anything `IsImplicitlyDeclared` (kills the backing field), and anything whose unwrapped return/type key ≠ `targetKey`.

Dedupe results by `(DeclaringType, Signature)` — a factory in a multi-targeted project appears once per compilation.

Sort: source before metadata, then declaring type, then signature.

**Step 4:** Run — PASS.

**Step 5:** Commit `feat(instantiation): solution-wide static factory discovery`.

---

### Task 5: Accessibility from a caller project

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetInstantiationOptionsLogic.cs`
- Test: `tests/RoslynCodeLens.Tests/GetInstantiationOptionsLogicTests.cs`

This is the highest-value and most error-prone part. `Compilation.IsSymbolAccessibleWithin` **throws `ArgumentException`** if the `within` symbol is not from that compilation or a referenced assembly — so the context symbol and the queried symbol must come from the *same* compilation.

**Step 1: Failing tests**

```csharp
private static (LoadedSolution, SymbolResolver) IvtWorkspace() =>
    RenameTestWorkspace.Create(
        ("Lib", new[] { ("Svc.cs", """
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Tests")]
            namespace Demo;
            public class Svc { public Svc() {} internal Svc(int a) {} private Svc(string s) {} }
            """) }),
        ("Tests", new[] { ("T.cs", "namespace T; public class Ctx {}") }),
        ("Stranger", new[] { ("S.cs", "namespace S; public class Ctx {}") }));

[Fact]
public void Internal_constructor_is_accessible_from_an_InternalsVisibleTo_project()
{
    var (loaded, resolver) = IvtWorkspace();
    var r = GetInstantiationOptionsLogic.Execute(loaded, resolver, "Svc", "Tests");
    Assert.True(Assert.Single(r.Constructors, c => c.Accessibility == "internal").Accessible);
}

[Fact]
public void Internal_constructor_is_not_accessible_from_an_unrelated_project()
{
    var (loaded, resolver) = IvtWorkspace();
    var r = GetInstantiationOptionsLogic.Execute(loaded, resolver, "Svc", "Stranger");
    Assert.False(Assert.Single(r.Constructors, c => c.Accessibility == "internal").Accessible);
}

[Fact]
public void Accessible_is_null_when_no_caller_project_given()
{
    var (loaded, resolver) = IvtWorkspace();
    var r = GetInstantiationOptionsLogic.Execute(loaded, resolver, "Svc", null);
    Assert.All(r.Constructors, c => Assert.Null(c.Accessible));
}

[Fact]
public void Unknown_fromProject_throws()
{
    var (loaded, resolver) = IvtWorkspace();
    var ex = Assert.Throws<McpToolException>(() =>
        GetInstantiationOptionsLogic.Execute(loaded, resolver, "Svc", "NoSuchProject"));
    Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
}
```

**Step 2:** Run — FAIL.

**Step 3: Implement**

- Resolve `fromProject` by project name; unknown → `McpToolException(ToolErrorCode.SymbolNotFound, ...)` listing available project names in `details`.
- Take that project's compilation. Re-resolve the target type **within that compilation** (by fully-qualified metadata name) so both symbols share a compilation. If the type is not visible from there at all, report every option `Accessible = false` rather than throwing.
- Use any type declared in that compilation as the `within` context (e.g. the first declared type in its global namespace); if the project declares no types, fall back to `compilation.Assembly`.
- `Accessible` stays `null` when `fromProject` is null.

**Step 4:** Run — PASS.

**Step 5:** Commit `feat(instantiation): caller-relative accessibility honouring InternalsVisibleTo`.

---

### Task 6: Extract and extend the DI scan

**Files:**
- Create: `src/RoslynCodeLens/Analysis/DiRegistrationScanner.cs`
- Modify: `src/RoslynCodeLens/Tools/GetDiRegistrationsLogic.cs` (delegate to the scanner)
- Test: `tests/RoslynCodeLens.Tests/DiRegistrationScannerTests.cs`

Move the body of `GetDiRegistrationsLogic.Execute` into the scanner **unchanged first**, confirm the existing `get_di_registrations` tests still pass, and only then extend. Do not do both in one step.

**Step 1: Failing tests for the new forms**

```csharp
private const string Startup = """
    using System;
    namespace Demo;
    public interface IFoo {}
    public class Foo : IFoo {}
    public class Startup
    {
        public void Configure(IServiceCollection s)
        {
            s.AddSingleton<IFoo, Foo>();      // already supported
            s.AddScoped<Foo>();               // single generic
            s.AddTransient(typeof(IFoo), typeof(Foo));  // typeof pair
            s.AddSingleton<IFoo>(sp => new Foo());      // factory lambda
        }
    }
    """;

[Fact] public void Finds_two_type_generic_registration() { ... Assert lifetime "Singleton" ... }
[Fact] public void Finds_single_generic_registration() { ... "Scoped", service == implementation ... }
[Fact] public void Finds_typeof_pair_registration() { ... "Transient" ... }
[Fact] public void Finds_factory_lambda_registration() { ... implementation resolved to Demo.Foo ... }
```

You will need a minimal `IServiceCollection` stub in the test source (an empty `public interface IServiceCollection {}` plus extension methods matching the names), since the fixture has no DI package reference. Give the stub extension methods the real names and shapes.

**Step 2:** Run — the three new forms FAIL, the generic one passes.

**Step 3: Implement**

Keep the existing generic-type-argument path. Add:
- **single generic** `Add*<TImpl>()` → service == implementation (already handled by the `typeArgs.Length == 1` branch — verify with a test rather than assuming);
- **typeof pairs**: invocation has two arguments, each a `TypeOfExpressionSyntax`; take `semanticModel.GetTypeInfo(arg.Type).Type`;
- **factory lambdas**: `Add*<TService>(sp => new Impl())` — service from the type argument, implementation from the single `ObjectCreationExpressionSyntax` in the lambda body. If the body is anything else (a method call, a conditional), record the service with implementation `"(factory)"` rather than guessing.

Route the walk through `SolutionScanner.EnumerateTrees`. **This is the change that broke `find_obsolete_usage` before** — see `2026-07-21-scan-migration-design.md`. It is safe here only because matching is by display string, not symbol identity. Pin it:

```csharp
[Fact]
public void Two_projects_sharing_a_file_path_do_not_lose_or_duplicate_registrations()
{
    // The dedupe key is (scope, path). Without a project-scoped discriminator, the second
    // project's registrations vanish; with no dedupe at all they double.
    var (loaded, resolver) = RenameTestWorkspace.Create(
        ("ProjA", new[] { ("Shared.cs", Startup) }),
        ("ProjB", new[] { ("Shared.cs", Startup) }));
    var results = DiRegistrationScanner.Scan(loaded, resolver, "Foo", default);
    // Both projects genuinely register Foo; each must be reported exactly once.
    Assert.Equal(2, results.Count(r => r.Lifetime == "Scoped"));
}
```

Decide the `scopeDiscriminator` from that test's outcome, not from reasoning: DI registrations are per-project facts, so the discriminator is the project name.

**Step 4:** Run the new tests **and** the existing DI tests:
`dotnet test --filter "FullyQualifiedName~DiRegistration|FullyQualifiedName~GetDiRegistrations"`
Expected: all PASS.

**Step 5:** Commit `feat(di): shared registration scanner with typeof, single-generic and factory-lambda forms`.

---

### Task 7: Wire DI into the instantiation result

**Files:** modify logic + tests.

`GetInstantiationOptionsLogic` calls `DiRegistrationScanner.Scan(...)` for the target type and puts the results in `DiRegistrations`. One test asserting a registered type surfaces its registration. Commit.

---

### Task 8: MCP tool wrapper

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetInstantiationOptionsTool.cs`
- Modify: `tools/DocGen/Program.cs` (categoryMap)

**Step 1: Write the wrapper**

```csharp
[McpServerToolType]
public static class GetInstantiationOptionsTool
{
    [McpServerTool(Name = "get_instantiation_options")]
    [Description(
        "Answer 'how do I construct this type?' in one call — accessible constructors, static " +
        "factory methods declared anywhere in the solution, and DI registrations. " +
        "Constructors report every parameter's type and name, declared accessibility, whether the " +
        "constructor is compiler-supplied (`isImplicit` — a struct or a class with no declared " +
        "constructor still has a usable parameterless one), and whether it is obsolete. " +
        "Factories are static members returning the type, including ones declared on a DIFFERENT " +
        "type such as `WidgetFactory.Create()`, with `Task<T>`/`ValueTask<T>` unwrapped and " +
        "flagged `isAsync`. Instance builder methods are excluded because the builder itself " +
        "would need constructing. " +
        "Pass `fromProject` to get `accessible` computed for that project, which honours " +
        "`InternalsVisibleTo` — this is how you find out whether your test project can reach an " +
        "internal constructor. Without it, `accessible` is null, meaning not computed. " +
        "`requiredMembers` lists members that must be set in an object initializer. " +
        "For interfaces, abstract classes and static classes, `instantiable` is false and `note` " +
        "explains why; use `find_implementations` to find concrete types.")]
    public static InstantiationOptionsResult Execute(
        MultiSolutionManager manager,
        [Description("Type to construct — simple (`Widget`) or fully qualified (`MyApp.Widget`).")]
            string symbol,
        [Description("Optional project name whose viewpoint decides `accessible` (e.g. `MyApp.Tests`).")]
            string? fromProject = null,
        CancellationToken cancellationToken = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GetInstantiationOptionsLogic.Execute(
            context.Loaded, context.Resolver, symbol, fromProject, cancellationToken);
    }
}
```

**Step 2:** Add to `tools/DocGen/Program.cs` categoryMap:

```csharp
["get_instantiation_options"] = "navigation",
```

**Step 3:** Run `dotnet test --filter "FullyQualifiedName~ToolDescriptionMdxSafety"` — this gates the `[Description]` for MDX-unsafe characters (raw `<`/`>` outside backticks). Expected PASS; if it fails, wrap the offending token in backticks.

**Step 4:** Commit `feat(instantiation): MCP tool wrapper`.

---

### Task 9: Fix the generate_test_skeleton fallback

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs:365-380`
- Test: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

Today `SutCreation` picks the fewest-parameter **public** constructor; when there is none it still emits `new Foo()`, which does not compile for a private-constructor type.

**Step 1: Failing test**

```csharp
[Fact]
public void Private_constructor_type_uses_a_factory_instead_of_uncompilable_new()
{
    // FactoryOnly has only a private ctor plus static Create(); `new FactoryOnly()` cannot compile.
    var result = ...generate skeleton for FactoryOnly...;
    Assert.DoesNotContain("new FactoryOnly()", result.Code);
    Assert.Contains("FactoryOnly.Create()", result.Code);
}
```

**Step 2:** Run — FAIL (emits `new FactoryOnly()`).

**Step 3:** In `SutCreation`, when no accessible constructor exists, consult the factory discovery from Task 4 and emit the first parameterless static factory. If there is none either, emit a TODO note naming the problem instead of uncompilable code.

**Step 4:** Run — PASS.

**Step 5:** Commit `fix(test-skeleton): don't emit uncompilable new() for private-constructor types`.

---

### Task 10: Fixture (MSBuild) test

**Files:** create `tests/RoslynCodeLens.Tests/GetInstantiationOptionsFixtureTests.cs`

Mirror `GetExtensionMethodsFixtureTests`: run the tool against the real `TestSolution` fixture and assert a known type's constructors are found. This catches integration breakage the AdhocWorkspace tests cannot. Commit.

---

### Task 11: Docs and counts

**Files:**
- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — add the tool to the appropriate section, following the existing entry style.
- `CLAUDE.md` — bump "66 code intelligence tools" to 67.
- `README.md` — add to the tool table if one lists tools individually.
- `docs/BACKLOG.md` — move `get_instantiation_options` from §5 medium tier to "Recently shipped"; record the follow-up that `find_similar_code`/cognitive complexity remain.

Commit `docs: document get_instantiation_options (67 tools)`.

---

### Task 12: Full verification before PR

```bash
dotnet build
dotnet test
git status --short tests/RoslynCodeLens.Tests/Fixtures/   # must be empty — fixture must stay pristine
```

Expected: clean build, all tests pass (~1051 existing + new).

**Then a high-effort review before the PR is opened** — every finding probed against real Roslyn, not argued. Prior features each surfaced ~4 genuine correctness bugs at this stage; reviewing after merge cost an extra hardening PR twice.

Review must specifically re-check:
1. Does any test still pass when its guard is deleted? (Vacuous tests shipped twice before.)
2. Is `Accessible` null-vs-false distinguished everywhere, including in the tool description?
3. Does the DI scanner lose results across multi-targeted projects? Run the shared-path test 20× — the previous scan migration failed on 9/20 runs, not 20/20.
