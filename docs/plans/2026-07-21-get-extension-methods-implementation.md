# get_extension_methods — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A `get_extension_methods` tool answering "which extension members apply to this type", covering solution source, referenced metadata (LINQ), and C# 14 extension blocks including properties.

**Architecture:** `GetExtensionMethodsLogic` resolves the receiver type, collects candidate static classes from the type's own project and its referenced assemblies, and tests each candidate two ways: `IsExtensionMethod` members via `ReduceExtensionMethod`, and nested `IsExtension` types for extension properties. `GetExtensionMethodsTool` is the thin MCP wrapper. Design doc: `docs/plans/2026-07-21-get-extension-methods-design.md` — **read it first**, especially the probe findings.

**Tech Stack:** Roslyn (existing deps), xUnit, `RenameTestWorkspace`.

**Working directory:** `.worktrees/extension-methods`, branch `feature/get-extension-methods`.

---

## READ THIS BEFORE WRITING ANY TEST — two fixture traps

`RenameTestWorkspace.CreateCore` (Fixtures/RenameTestWorkspace.cs:79-80) currently gives every project **only** `MetadataReference.CreateFromFile(typeof(object).Assembly.Location)` — corelib alone — and sets **no parse options**.

1. **A LINQ test will fail against that fixture, and the tool will not be at fault.** With a hand-picked reference closure, `Enumerable`'s extension methods do not reduce against `List<int>`; with the real closure they do. This exact false negative appeared during design probing and nearly caused the feature to be designed around "LINQ is unavailable". **If a LINQ assertion fails, suspect the fixture's references before the tool.**
2. **C# 14 `extension` blocks need `LanguageVersion.Preview`** on the parse options. Without it the source is a syntax error, and a test asserting "no extension properties found" would pass for entirely the wrong reason.

Task 1 fixes both before any feature test is written.

---

### Task 1: Extend the test fixture

**Files:** Modify `tests/RoslynCodeLens.Tests/Fixtures/RenameTestWorkspace.cs`.

Add two opt-in capabilities without disturbing existing callers (every current call must keep compiling and behaving identically — the whole suite is the regression net):

- **Full framework references.** Add a `CreateWithFrameworkRefs(...)` entry point (or an optional flag threaded into `CreateCore`) that references the full trusted-platform-assemblies closure instead of corelib alone:
  ```csharp
  var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
      .Split(Path.PathSeparator)
      .Where(p => p.EndsWith(".dll", StringComparison.Ordinal))
      .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
  ```
  Document on it *why* it exists: the default corelib-only closure makes BCL extension methods silently unreducible.
- **Parse options.** Allow `LanguageVersion.Preview` so C# 14 `extension` blocks parse. Documents are added via `SourceText`, so the parse options must reach `ProjectInfo` (`.WithParseOptions(new CSharpParseOptions(LanguageVersion.Preview))`).

**Verification test** (write it in `tests/RoslynCodeLens.Tests/Fixtures/RenameTestWorkspaceTests.cs` or alongside):
1. A workspace built with framework refs compiles `using System.Linq; ... new List<int>().Where(x => true)` with **zero** error diagnostics — proving the closure is real rather than assumed.
2. A workspace built with preview parse options compiles an `extension(int value) { public int Tripled => value * 3; }` block with **zero** error diagnostics.

Both must fail before the fixture change. Commit: `test: fixture support for framework references and C# 14 syntax`.

---

### Task 2: Model

**Files:** Create `src/RoslynCodeLens/Models/ExtensionMemberInfo.cs`.

```csharp
namespace RoslynCodeLens.Models;

/// <summary>
/// One extension member applicable to a queried receiver type.
/// Kind: method | property. Signature is the REDUCED, call-site form — what a caller types
/// (`Where&lt;int&gt;(Func&lt;int, bool&gt;)`), not the declared form.
/// Namespace is always reported because applicability does not imply the `using` is present.
/// </summary>
public record ExtensionMemberInfo(
    string Name,
    string Kind,
    string Signature,
    string DeclaringType,
    string Namespace,
    string Origin,
    string? File,
    int? Line,
    string? XmlDocSummary);
```

Build → commit `feat: ExtensionMemberInfo model`.

---

### Task 3: GetExtensionMethodsLogic (TDD — the matrix)

**Files:** Create `tests/RoslynCodeLens.Tests/GetExtensionMethodsLogicTests.cs`, `src/RoslynCodeLens/Tools/GetExtensionMethodsLogic.cs`.

Signature: `Execute(LoadedSolution loaded, SymbolResolver resolver, MetadataSymbolResolver metadata, string type, string? nameFilter)` → `IReadOnlyList<ExtensionMemberInfo>`.

**Algorithm**

1. Resolve the receiver type: `resolver.FindSymbols(type)` → first `ITypeSymbol`; else `metadata.Resolve(type)?.Symbol as ITypeSymbol`. Null → `SymbolNotFound`. A non-type (namespace, method) → `InvalidArgument`.
2. Determine the owning compilation: the one containing the type's declaration, or (for metadata types) any compilation referencing it. Candidate static classes come from **that compilation only** — its own source types plus the types of assemblies it references. An extension in a project the receiver's project does not reference is not callable and must not be reported.
3. For each candidate static class (`IsStatic && TypeKind == Class`):
   - **Pass A (methods):** members where `IsExtensionMethod`; call `ReduceExtensionMethod(receiver)`. Non-null ⇒ applicable; use the reduced symbol for `Signature` and `Name`.
   - **Pass B (properties):** nested types where `INamedTypeSymbol.IsExtension`; for each, determine the block's receiver parameter type and test assignability from the queried type; if it applies, report the block's **properties** with `Kind: "property"`.
4. Apply `nameFilter` (case-insensitive substring on `Name`) if given.
5. `Origin` is `"source"` when the declaring type has a source location, else `"metadata"`; `File`/`Line` only for source. `XmlDocSummary` from the symbol's documentation when present.
6. Sort: source before metadata, then declaring type, then name.

**Tests** — use the framework-refs fixture from Task 1 for anything touching BCL:

1. `SimpleExtension_AppliesToItsReceiver` — `this int` extension found for `int`.
2. `SimpleExtension_DoesNotApplyToOtherTypes` — that same extension NOT found for `string`.
3. `GenericExtension_AppliesViaInference` — `this IEnumerable<T>` found for `List<int>`.
4. `GenericExtension_AppliesToString` — same found for `string` (string is `IEnumerable<char>`).
5. `MismatchedGenericExtension_DoesNotApply` — `this IEnumerable<string>` NOT found for `string`. **This is the discriminating case from the probe; a naive "does the name match" implementation passes 1-4 and fails this.**
6. `BclLinq_IsReported` — `List<int>` yields `Where` with `Origin: "metadata"`. (Framework-refs fixture — see the trap note.)
7. `CSharp14BlockMethod_IsReported` — `extension(int v) { public int Thrice() ... }` found for `int`.
8. `CSharp14BlockProperty_IsReported` — `extension(int v) { public int Tripled => ... }` found with `Kind: "property"`. **This is the test that fails if the `IsExtension` nested-type walk is dropped; the probe showed such properties are invisible to `IsExtensionMethod`.**
9. `NameFilter_Narrows` — filter `"chunk"` returns only matching names, case-insensitively.
10. `SourceExtensionsSortBeforeMetadata` — a solution extension on `List<int>` precedes the LINQ ones.
11. `UnreferencedProjectExtension_IsNotReported` — two projects with NO reference between them; the other project's extension is absent. (`RenameTestWorkspace.Create`'s multi-project overload references earlier projects, so use `CreateChain` or construct so the receiver's project genuinely does not reference the extension's.)
12. `UnknownType_Throws` → `SymbolNotFound`; `NamespaceArgument_Throws` → `InvalidArgument`.
13. `Origin_And_Location_AreCorrect` — a source extension carries `File`/`Line`; a metadata one carries neither.

**Step order:** write tests → red run (missing type) → implement → green. Do not weaken test 5, 8 or 11; each pins a specific way the naive implementation is wrong.

Commit `feat: get_extension_methods applicability over source and metadata`.

---

### Task 4: Tool wrapper + fixture test

**Files:** Create `src/RoslynCodeLens/Tools/GetExtensionMethodsTool.cs`, `tests/RoslynCodeLens.Tests/GetExtensionMethodsFixtureTests.cs`.

Wrapper mirrors `FindReferencesTool`: `EnsureLoaded()`, `GetAnalysisContext()`, envelope via `ToolListResult.Create` with summary `{ byOrigin, byDeclaringType }`, `limit` default 100. The `[Description]` must say that results are not filtered by `using` scope — the namespace is reported so the caller can add the import — and must pass `ToolDescriptionMdxSafetyTests` (backtick every code token; note `IEnumerable<T>` needs backticks).

Fixture test against `TestSolutionFixture`: query a type from the real solution and assert LINQ extensions appear with `Origin: "metadata"`. Read the fixture's types first rather than guessing.

Commit `feat: expose get_extension_methods MCP tool`.

---

### Task 5: Docs + verification

- SKILL.md: Red Flags row `| "What can I call on this type?" / "Is there an extension method for X?" | get_extension_methods |`; a bullet near `get_overloads` covering applicability, that both source and BCL extensions are reported, that C# 14 blocks (methods and properties) are included, and that results are NOT filtered by `using` scope; Quick Reference row; metadata-support row (`Yes — reports both source and metadata extensions`).
- CLAUDE.md: 65 → **66**.
- `tools/DocGen/Program.cs` categoryMap: `["get_extension_methods"] = "navigation"`.
- README.md: one bullet in the existing style.
- `docs/BACKLOG.md`: medium-tier bullet → ✅ shipped (PR #n) + Recently-shipped row.
- Full `dotnet build` + `dotnet test` (~1030 expected; ~7-11 min — run in background). Fixture-pristine check.

Commit `docs: document get_extension_methods (66 tools)`.

---

## Deviations
Report: whether the fixture needed more than the two capabilities in Task 1; how the C# 14 extension-block receiver type was obtained (the nested type's shape was probed but not its exact receiver accessor); any case where candidate-scope selection proved harder than "the receiver's compilation"; and the real cost of scanning referenced assemblies on the TestSolution fixture.
