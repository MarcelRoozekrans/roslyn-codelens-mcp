# Dead-Code False-Positive Filters — Design

**Date:** 2026-05-22
**Status:** Approved, ready for implementation plan
**Motivation:** `find_unused_symbols` today flags too many false positives — test methods, MCP tool entry points, source-generator output, MEF-composed services. Adopt the attribute-based filter approach from `sailro/RoslynMcpExtension` (MIT, safe to reimplement) to improve precision. Most critically, **our own MCP tools' `Execute` methods get falsely flagged today**, so this is a dogfooding fix as much as a precision win.

## Goals

- Filter out symbols that ARE used, just through mechanisms `find_references` can't see: test framework discovery, MCP attribute scanning, source-generation, MEF composition, interop layout.
- Surface filter counts in the envelope `summary` so the caller knows the filtering happened.
- Keep `find_unused_symbols` behavior identical for symbols outside these categories — same accessibility rules, same override/interface skip, same project filter, same `includeInternal` semantics.

## Non-goals

- No WPF/XAML reference scanning (irrelevant for MCP-server target codebases; sailro pays disk I/O on every call for it).
- No COM/VS-extension framework-activated-type detection (`GuidAttribute`, `ToolWindowPane`, etc.).
- No solution-wide derived-class walk to detect inverted-inheritance test bases (`TestTypeIndex` in sailro). Walking only the direct `BaseType` chain is cheaper and covers the common case; if reports come in, we add it later.
- No `disableFilters` escape-hatch parameter. The filters are correctness improvements, not preferences.

## Architecture

A new internal static class `DeadCodeFilters` (in `src/RoslynCodeLens/Tools/`) owns:

1. Attribute-name constants (six families).
2. A `Classify(ISymbol) → Reason` function returning one of seven values: `None`, `TestMethod`, `TestContainer`, `McpTool`, `Generated`, `Composition`, `Interop`.

```csharp
internal static class DeadCodeFilters
{
    public enum Reason { None, TestMethod, TestContainer, McpTool, Generated, Composition, Interop }

    public static Reason Classify(ISymbol symbol);
}
```

`FindUnusedSymbolsLogic` calls `Classify` after its existing `ShouldSkipType`/`ShouldSkipMember` checks. If `Reason != None`, the symbol is skipped and a per-reason counter is incremented.

`FindUnusedSymbolsLogic.Execute` returns a tuple so the Tool wrapper can build the summary:

```csharp
public static (IReadOnlyList<UnusedSymbolInfo> Items, IReadOnlyDictionary<string, int> FilteredCounts)
    Execute(LoadedSolution loaded, SymbolResolver resolver, string? project, bool includeInternal);
```

The `FilteredCounts` keys are camelCase strings matching the JSON shape (`testMethod`, `testContainer`, `mcpTool`, `generated`, `composition`, `interop`).

## Attribute lists

All names matched **by simple name (with/without `Attribute` suffix) AND by full name**. The match also walks the attribute class's own `BaseType` chain so custom attributes that inherit from these are caught.

**TestMethod attributes** (member-level → filter the method):
- xUnit: `Fact`, `Theory`, `InlineData`, `MemberData`, `ClassData`
- NUnit: `Test`, `TestCase`, `TestCaseSource`, `Values`, `ValueSource`, `Range`, `Random`, `Combinatorial`, `Pairwise`, `Sequential`, `Datapoint`, `DatapointSource`, `SetUp`, `TearDown`, `OneTimeSetUp`, `OneTimeTearDown`
- MSTest: `TestMethod`, `DataTestMethod`, `DataRow`, `DynamicData`, `TestInitialize`, `TestCleanup`, `ClassInitialize`, `ClassCleanup`, `AssemblyInitialize`, `AssemblyCleanup`

**TestContainer attributes** (type-level → filter the type AND its members):
- `TestClass`, `TestFixture`, `TestFixtureSource`, `Collection`, `CollectionDefinition`

A member is treated as `TestContainer`-filtered if any type in its `containingType.BaseType` chain matches a container attribute OR contains a TestMethod-attributed method. This catches base test classes without their own attribute.

**MCP attributes** (filter type and members):
- `McpServerTool`, `McpServerToolType`

**Generated-code attributes** (filter symbol or its containing type):
- `CompilerGenerated`, `GeneratedCode`, `DebuggerNonUserCode`

**Composition (MEF) attributes** (filter symbol or its containing type):
- `Export`, `InheritedExport`, `Import`, `ImportMany`, `ImportingConstructor`

**Interop attributes** (filter fields only):
- On field: `FieldOffset`, `MarshalAs`
- On containing struct: `StructLayout`, `InlineArray`

## Tool / summary changes

`FindUnusedSymbolsTool.cs` extends the existing summary aggregate:

```jsonc
// Before:
{ "byKind": { "Class": 3, "Method": 8, "Field": 2 } }

// After:
{
  "byKind": { "Class": 3, "Method": 8, "Field": 2 },
  "filteredOut": {
    "testMethod": 12,
    "testContainer": 4,
    "mcpTool": 8,
    "generated": 3,
    "composition": 0,
    "interop": 0
  }
}
```

All six `filteredOut` keys are always present (even at zero) for a stable schema.

No new parameters on the tool. No behavior changes for symbols that don't match a filter.

## Test strategy

**Three layers:**

1. **`DeadCodeFiltersTests`** (new) — pure unit tests with manufactured `ISymbol` instances via inline compilations. ~15 tests, one per attribute family + edge cases (Attribute-suffix-stripping, inherited attribute via BaseType walk, MCP attribute on `Execute` methods, generated-code attribute on type vs member).
2. **`FindUnusedSymbolsLogicTests` extension** — fixture-based:
   - **MCP self-test (dogfooding gate):** assert no `*Tool.Execute` method appears in unused results when scanning our own source.
   - **Test framework filter:** the existing `XUnitFixture` / `NUnitFixture` / `MSTestFixture` projects already have annotated test methods — assert none appear unused.
   - **Generated / MEF / Interop:** small new fixture files in `TestSolution` exercising each attribute family. Assert filtered.
3. **`FindUnusedSymbolsToolTests` extension** — one test asserting the envelope `summary.filteredOut` has all six keys with reasonable non-negative counts.

**Acceptance gate (manual smoke test, post-merge):**
- Run `find_unused_symbols` against this repo via the MCP server.
- Confirm zero `*Tool.Execute` methods, zero test methods, zero source-generator output appear.

## Risks and accepted trade-offs

- **No XAML support.** WPF projects using code-behind would still see false positives. Out of scope; document as a known limitation if anyone asks.
- **Inverted-inheritance test bases not caught.** A `MyBaseTests` with no `[TestClass]` whose subclass `MyConcreteTests` has the attribute — members of MyBaseTests get flagged. Workaround: put the attribute on the base too. We'll add solution-wide derived-class detection only if real reports surface.
- **Custom attribute inheritance is shallow-checked.** We walk the attribute class's BaseType chain, but if someone names their attribute `MySpecialFact` deriving from a non-listed base, we won't catch it. The framework-vendor attribute names are well-known; custom subclasses are rare.
- **Hardcoded attribute lists.** Adding new test frameworks (e.g. xUnit v3) requires a code change. Acceptable — the list is small and central.
