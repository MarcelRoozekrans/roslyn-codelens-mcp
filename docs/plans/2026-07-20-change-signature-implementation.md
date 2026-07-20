# change_signature — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A `change_signature` MCP tool that adds, removes, and reorders a method's parameters and updates every call site, driving Roslyn's internal change-signature engine through one isolated reflection bridge.

**Architecture:** `Analysis/ChangeSignatureBridge.cs` owns **every** reflection call and exposes one typed method. `ChangeSignatureLogic` resolves the method, validates operations, calls the bridge, computes conflicts + the cascade set, and returns edits. `ChangeSignatureTool` is the thin MCP wrapper. Safety reuses the shipped write path unchanged. Design doc: `docs/plans/2026-07-20-change-signature-design.md` — **read it first**, especially the verified-internals section.

**Tech Stack:** Roslyn 5.6 (existing deps; internal APIs via reflection), xUnit, `RenameTestWorkspace`.

**Working directory:** `.worktrees/change-signature`, branch `feature/change-signature`. All commands from its root.

---

## Verified Roslyn internals — use these exactly, do not re-derive

All probed against Microsoft.CodeAnalysis 5.6. `AbstractChangeSignatureService` and friends live in `Microsoft.CodeAnalysis.Features`; `SemanticDocument` in `Microsoft.CodeAnalysis.Workspaces`.

| Member | Signature |
|---|---|
| `SemanticDocument.CreateAsync` | `static Task<SemanticDocument>(Document, CancellationToken)` — in Workspaces. **The type is `internal`** (an earlier note here said public; it is not), so bind with `BindingFlags.NonPublic`. |
| `ParameterConfiguration.Create` | `static ParameterConfiguration(ImmutableArray<Parameter>, bool isExtensionMethod, int selectedIndex)` — derives the `this`/`params`/default split itself; **prefer this over the 5-arg ctor** |
| `ExistingParameter` | `ctor(IParameterSymbol)` |
| `AddedParameter` | `ctor(ITypeSymbol type, string typeName, string name, CallSiteKind, string callSiteValue, bool isRequired, string defaultValue, bool typeBinds)` |
| `CallSiteKind` | enum: `Value, ValueWithName, Todo, Omitted, Inferred` — use `Value` for an explicit call-site value, `Omitted` when the parameter is optional and existing calls should skip it |
| `SignatureChange` | `ctor(ParameterConfiguration originalConfiguration, ParameterConfiguration updatedConfiguration)` |
| `ChangeSignatureOptionsResult` | `ctor(SignatureChange updatedSignature, bool previewChanges)` |
| `ChangeSignatureAnalysisSucceededContext` | `ctor(SemanticDocument, int positionForTypeBinding, ISymbol, ParameterConfiguration)` |
| `AbstractChangeSignatureService.ChangeSignatureWithContextAsync` | `Task<ChangeSignatureResult>(ChangeSignatureAnalyzedContext, ChangeSignatureOptionsResult, CancellationToken)` — **internal instance method; this is the solution-wide entry point** |
| `ChangeSignatureResult` | `.Succeeded`, `.UpdatedSolution`, `.ChangeSignatureFailureKind`, `.ConfirmationMessage` |
| `CSharpChangeSignatureService` | in `Microsoft.CodeAnalysis.CSharp.Features`; parameterless ctor, `[ExportLanguageService]` |

**DO NOT use `AbstractChangeSignatureService.ChangeSignature(...)`.** It looks like the entry point and is even public, but it returns a `SyntaxNode` — it rewrites the declaration only and would leave every call site broken.

`Parameter` is the abstract base of `ExistingParameter`/`AddedParameter`; build `ImmutableArray<Parameter>` reflectively (create a typed array via `Array.CreateInstance(parameterType, n)` then `ImmutableArray.Create`-equivalent, or call the generic `ImmutableArray.CreateRange<T>` via `MakeGenericMethod`).

---

### Task 1: Models

**Files:** Create `src/RoslynCodeLens/Models/SignatureOperation.cs`, `ChangeSignatureResult.cs`.

```csharp
namespace RoslynCodeLens.Models;

/// <summary>
/// One edit to a parameter list, applied in order against the original parameters.
/// Kind: remove | reorder | add.
/// remove  → Parameter names the parameter to drop.
/// reorder → Order is a full permutation of the names surviving at that point.
/// add     → Name/Type plus CallSiteValue (required: what every existing call site passes).
///           DefaultValue makes the parameter optional, letting existing calls omit it instead.
/// </summary>
public record SignatureOperation(
    string Kind,
    string? Parameter = null,
    IReadOnlyList<string>? Order = null,
    string? Name = null,
    string? Type = null,
    string? CallSiteValue = null,
    string? DefaultValue = null);
```

```csharp
namespace RoslynCodeLens.Models;

public record ChangeSignatureResult(
    bool Success,
    string Method,
    string OldSignature,
    string NewSignature,
    bool Applied,
    IReadOnlyList<TextEdit> Edits,
    int FilesChanged,
    IReadOnlyList<string> CascadedTo,
    IReadOnlyList<RenameConflict> Conflicts,
    string Message);
```

`RenameConflict` already exists (`{ Id, Message, File, Line }`) — reuse it, do not clone.

Build → commit `feat: change_signature models`.

---

### Task 2: ChangeSignatureBridge (TDD — probe first)

**Files:** Create `tests/RoslynCodeLens.Tests/ChangeSignatureBridgeTests.cs`, `src/RoslynCodeLens/Analysis/ChangeSignatureBridge.cs`.

**Step 1: the probe test comes first — it is the early-warning system for a Roslyn upgrade.**

```csharp
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests;

public class ChangeSignatureBridgeTests
{
    /// <summary>
    /// Every internal Roslyn member the bridge needs must resolve. If a Roslyn upgrade moves or
    /// renames one, this fails loudly here rather than at a user's apply — which is the whole
    /// reason the reflection is confined to one probeable surface.
    /// </summary>
    [Fact]
    public void Probe_ResolvesEveryRequiredMember()
    {
        var missing = ChangeSignatureBridge.Probe();
        Assert.Empty(missing);
    }
}
```

**Step 2:** run `--filter "FullyQualifiedName~ChangeSignatureBridge"` → FAIL (type missing).

**Step 3: implement the bridge.** Shape (fill in against the table above):

```csharp
namespace RoslynCodeLens.Analysis;

/// <summary>Outcome of Roslyn's own change-signature engine, with its verdict preserved.</summary>
public sealed record BridgeResult(bool Succeeded, Solution? UpdatedSolution, string? FailureMessage);

/// <summary>Describes one parameter of the target signature, in final order.</summary>
public sealed record DesiredParameter(
    IParameterSymbol? Existing,                 // null for an added parameter
    string? Name, string? TypeName, ITypeSymbol? Type,
    string? CallSiteValue, string? DefaultValue);

/// <summary>
/// The ONLY place that touches Roslyn's internal change-signature API. Everything reflective is
/// resolved once and cached; <see cref="Probe"/> reports anything missing so a Roslyn upgrade
/// fails loudly instead of degrading into partial work.
/// </summary>
public static class ChangeSignatureBridge
{
    // Lazy<Members> resolving: the two assemblies, each type in the table, each ctor/method.
    // Probe() returns the names of anything unresolved (empty list = healthy).
    public static IReadOnlyList<string> Probe() { /* ... */ }

    public static async Task<BridgeResult> ChangeSignatureAsync(
        Document document, IMethodSymbol method,
        IReadOnlyList<DesiredParameter> updated, CancellationToken ct)
    {
        // 1. SemanticDocument.CreateAsync(document, ct)
        // 2. original = ParameterConfiguration.Create([ExistingParameter(p) for p in method.Parameters],
        //                                            method.IsExtensionMethod, selectedIndex: 0)
        // 3. updatedConfig = ParameterConfiguration.Create(mapped `updated`, method.IsExtensionMethod, 0)
        //      ExistingParameter(sym) for survivors;
        //      AddedParameter(type, typeName, name,
        //          DefaultValue == null ? CallSiteKind.Value : CallSiteKind.Omitted,
        //          callSiteValue, isRequired: DefaultValue == null, defaultValue, typeBinds: type != null)
        // 4. change = SignatureChange(original, updatedConfig)
        // 5. context = ChangeSignatureAnalysisSucceededContext(semanticDoc, positionForTypeBinding, method, original)
        //    positionForTypeBinding: the DECLARATION's span start, mirroring the IDE flow.
        //    (Originally justified as necessary for added type names to bind — that was tested
        //    during Task 3 by flipping it to 0, and every Add test still passed. What actually
        //    makes CancellationToken render is resolving a real ITypeSymbol and passing
        //    typeBinds:true; the Simplifier reduces it against the target document.)
        // 6. options = ChangeSignatureOptionsResult(change, previewChanges: false)
        // 7. service = CSharpChangeSignatureService instance (Activator.CreateInstance, or the
        //    workspace language service if direct construction misbehaves — try direct first)
        // 8. result = await (Task)ChangeSignatureWithContextAsync.Invoke(service, [context, options, ct])
        // 9. read .Succeeded / .UpdatedSolution / .ConfirmationMessage into BridgeResult
    }
}
```

Every reflective failure throws `McpToolException(ToolErrorCode.Internal, "...", new { member })` naming the member. Never swallow.

**Step 4:** probe test green.

**Step 5:** add a real end-to-end bridge test before moving on — a two-parameter method with one call site, reordered:

```csharp
[Fact]
public async Task Reorder_RewritesDeclarationAndCallSite()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Demo.cs", """
        namespace Demo;
        public class Svc
        {
            public int Add(int a, string b) => a;
            public int Use() => Add(1, "x");
        }
        """));
    var method = (IMethodSymbol)resolver.FindSymbols("Svc.Add").Single();
    var doc = loaded.Solution.GetDocument(
        method.DeclaringSyntaxReferences[0].SyntaxTree)!;

    var reordered = new[] { method.Parameters[1], method.Parameters[0] }
        .Select(p => new DesiredParameter(p, null, null, null, null, null)).ToList();

    var result = await ChangeSignatureBridge.ChangeSignatureAsync(doc, method, reordered, default);

    Assert.True(result.Succeeded, result.FailureMessage);
    var text = (await result.UpdatedSolution!.GetDocument(doc.Id)!.GetTextAsync()).ToString();
    Assert.Contains("Add(string b, int a)", text, StringComparison.Ordinal);
    Assert.Contains("Add(\"x\", 1)", text, StringComparison.Ordinal);   // call site followed
}
```

**If this test does not rewrite the call site, stop and report — the entry point is wrong and the rest of the plan is void.** That assertion is the plan's single most important check.

Commit `feat: ChangeSignatureBridge over Roslyn's internal engine`.

---

### Task 3: ChangeSignatureLogic (TDD)

**Files:** Create `tests/RoslynCodeLens.Tests/ChangeSignatureLogicTests.cs`, `src/RoslynCodeLens/Tools/ChangeSignatureLogic.cs`.

Signature: `ExecuteAsync(LoadedSolution loaded, SymbolResolver resolver, string method, IReadOnlyList<SignatureOperation> operations, bool preview, bool force, CommitWrittenDocuments? commitToMemory, CancellationToken ct)`.

Flow: resolve method (overload group with >1 member ⇒ `AmbiguousMatch` listing signatures) → degraded-load guard (as `RenameSymbolLogic`) → apply operations to build `DesiredParameter[]` → bridge → if `!Succeeded` return failure carrying Roslyn's message → extract edits + conflicts (reuse the count-based diagnostics diff already in `RenameSymbolLogic`; extract it to a shared helper rather than copying — reviewers have flagged duplication repeatedly) → cascade set via `SymbolFinder.FindOverridesAsync` + `FindImplementationsAsync` on the original symbol → preview/apply gates → `SolutionChangeWriter.WriteChangesToDiskAsync` → `SolutionChangeWriter.CommitAsync`.

**Operation application** against `method.Parameters`:
- `remove`: drop by name; unknown name ⇒ `SymbolNotFound`.
- `reorder`: `Order` must be exactly the multiset of surviving names ⇒ else `InvalidArgument` naming the difference.
- `add`: `Name`, `Type`, `CallSiteValue` all required ⇒ else `InvalidArgument`; resolve `Type` via `SymbolResolver`/`MetadataSymbolResolver`, and if it doesn't resolve pass `typeBinds: false` with the literal type name (Roslyn supports that).

**Tests** (fixture source with an interface, an implementor, an override, an extension method, named-argument and optional-parameter call sites):

1. `Remove_DropsParameterAndUpdatesCallSites`
2. `Reorder_RewritesCallSitesInNewOrder`
3. `Add_InsertsCallSiteValueEverywhere` — `callSiteValue: "CancellationToken.None"` appears at each call site
4. `Add_WithDefault_LeavesExistingCallSitesAlone` — `defaultValue` set ⇒ `CallSiteKind.Omitted`; call sites unchanged, declaration gains an optional parameter
5. `NamedArguments_AreRewritten` — a call site using `name:` syntax survives a reorder correctly
6. `InterfaceImplementation_IsCascaded` — changing the interface method updates the implementor, and `CascadedTo` names it
7. `Override_IsCascaded`
8. `ExtensionMethod_ThisParameterPreserved` — reorder of an extension method keeps `this` first
9. `RemovedParameterStillUsed_ProducesConflict` — removing a parameter the body references ⇒ `Conflicts` non-empty, apply refused without `force`
10. `Overloads_AreAmbiguous` → `AmbiguousMatch`
11. `UnknownMethod` → `SymbolNotFound`; `UnknownParameter` → `SymbolNotFound`
12. `ReorderNotAPermutation` → `InvalidArgument`; `AddWithoutCallSiteValue` → `InvalidArgument`; `EmptyOperations` → `InvalidArgument`
13. `Preview_LeavesDiskUntouched` / `Apply_WritesAndCommits` (temp-dir workspace, mirroring the rename tests)
14. `RoslynRefusal_IsSurfaced` — if a case exists where Roslyn returns `Succeeded: false`, assert its message reaches `Message`; if none is constructible, note it in the report rather than faking one.

Commit `feat: change_signature resolution, operations, and safety gates`.

---

### Task 4: Tool wrapper + fixture test

**Files:** Create `src/RoslynCodeLens/Tools/ChangeSignatureTool.cs`, `tests/RoslynCodeLens.Tests/ChangeSignatureFixtureTests.cs`.

Wrapper mirrors `RenameSymbolTool`: `manager.EnsureLoaded()`, `GetAnalysisContext()`, passes `manager.CommitDocumentTextsAsync`. Parameters: `method`, `operations` (array of the model), `preview = true`, `force = false`. The `[Description]` must explain each operation kind and that `callSiteValue` is required for `add` — and must pass `ToolDescriptionMdxSafetyTests` (backtick every code token; no bare `<`/`{`).

Fixture test: preview-only against `TestSolutionFixture` so fixture files stay pristine — reorder `Greeter.Greet`'s parameters (check its real signature first) and assert edits span the declaration and its test call sites.

Commit `feat: expose change_signature MCP tool`.

---

### Task 5: Docs + verification

- SKILL.md: Red Flags row (`| "Add/remove/reorder a parameter" / "Let me update all the call sites by hand" | change_signature |`), a bullet beside `rename_symbol` covering operations, `callSiteValue`, the cascade report and the safety gates, a Quick Reference row, and a metadata-support row (source only).
- CLAUDE.md: 63 → **64**.
- `tools/DocGen/Program.cs` categoryMap: `["change_signature"] = "diagnostics"`.
- README.md: one bullet in the existing style.
- `docs/BACKLOG.md`: medium-tier bullet → ✅ shipped (PR #n) + a Recently-shipped row.
- Full `dotnet build` + `dotnet test` (~880 expected; the suite takes ~8-12 min — run it in the background). Fixture-pristine check.

Commit `docs: document change_signature (64 tools)`.

---

## Deviations
Report every Roslyn API mismatch, whether direct `Activator.CreateInstance` of the service worked or the workspace language service was needed, the Task 2 Step 5 outcome (call-site rewriting — the make-or-break check), and anything about cascade reporting that `SymbolFinder` couldn't answer.
