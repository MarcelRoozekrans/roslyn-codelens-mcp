# check_architecture — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A `check_architecture` tool that evaluates user-supplied layering rules against the solution's real semantic dependencies and reports grouped violations with sample sites.

**Architecture:** `Analysis/ScopePattern.cs` is a pure matcher (exact / prefix-wildcard / `*`). `CheckArchitectureLogic` walks source documents — skipping any whose scopes match no rule's `from`, which is what keeps cost proportional to the rules rather than the solution — resolves each referencing node through the semantic model, and accumulates `(rule, sourceScope → targetScope)` groups. `CheckArchitectureTool` validates rules and wraps the envelope. Design doc: `docs/plans/2026-07-20-check-architecture-design.md` — **read it first**, especially the two semantics that decide whether the tool is usable.

**Tech Stack:** Roslyn (existing deps), xUnit, `RenameTestWorkspace` (has multi-project `Create` and strict-chain `CreateChain` overloads).

**Working directory:** `.worktrees/check-architecture`, branch `feature/check-architecture`.

**Conventions:** `McpToolException(ToolErrorCode.X, msg, details)`; `ToolListResult.Create(items, limit, summary)`; string kinds; generated code skipped via `GeneratedCodeDetector.IsGenerated(tree)` as the first statement of the tree loop (see `FindThrowSitesLogic.cs:50`); tool `[Description]` must pass `ToolDescriptionMdxSafetyTests` (backtick every code token, no bare `<`/`{`); commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: Models

**Files:** Create `src/RoslynCodeLens/Models/ArchitectureRule.cs`, `ArchitectureViolation.cs`.

```csharp
namespace RoslynCodeLens.Models;

/// <summary>
/// One layering rule. Kind: forbid | allowOnly.
/// forbid    → a dependency from `From` to any scope matching `To` is a violation.
/// allowOnly → a dependency from `From` to any solution-internal scope NOT in `To` is a violation.
/// Patterns are exact ("Demo.Domain"), prefix-wildcard ("Demo.Domain.*"), or "*".
/// </summary>
public record ArchitectureRule(
    string Kind,
    string From,
    IReadOnlyList<string> To,
    string? Description = null);
```

```csharp
namespace RoslynCodeLens.Models;

public record ViolationSite(string File, int Line, int Column, string SourceSymbol, string TargetSymbol);

/// <summary>
/// One violated (rule, sourceScope → targetScope) edge. ReferenceCount is the full count;
/// Sites carries only the first maxSitesPerViolation of them.
/// </summary>
public record ArchitectureViolation(
    string RuleKind,
    string? RuleDescription,
    string FromPattern,
    string ToPattern,
    string SourceScope,
    string TargetScope,
    int ReferenceCount,
    IReadOnlyList<ViolationSite> Sites);
```

Build → commit `feat: check_architecture models`.

---

### Task 2: ScopePattern (TDD — pure matcher)

**Files:** Create `tests/RoslynCodeLens.Tests/ScopePatternTests.cs`, `src/RoslynCodeLens/Analysis/ScopePattern.cs`.

**Step 1: failing tests**

```csharp
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests;

public class ScopePatternTests
{
    [Theory]
    // exact
    [InlineData("Demo.Domain", "Demo.Domain", true)]
    [InlineData("Demo.Domain", "Demo.Domain.Orders", false)]   // exact does NOT match children
    [InlineData("Demo.Domain", "Demo.DomainX", false)]
    // prefix wildcard: the scope itself AND everything beneath it
    [InlineData("Demo.Domain.*", "Demo.Domain", true)]
    [InlineData("Demo.Domain.*", "Demo.Domain.Orders", true)]
    [InlineData("Demo.Domain.*", "Demo.Domain.Orders.Rules", true)]
    [InlineData("Demo.Domain.*", "Demo.DomainX", false)]        // not a segment boundary
    [InlineData("Demo.Domain.*", "Demo.Infrastructure", false)]
    // match-all
    [InlineData("*", "Anything.At.All", true)]
    [InlineData("*", "", true)]
    public void Matches(string pattern, string scope, bool expected)
        => Assert.Equal(expected, ScopePattern.Matches(pattern, scope));

    [Theory]
    [InlineData("Demo.*.Orders")]   // interior wildcard unsupported
    [InlineData("*.Orders")]        // suffix wildcard unsupported
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsMalformedPatterns(string pattern)
        => Assert.False(ScopePattern.IsValid(pattern));

    [Theory]
    [InlineData("Demo.Domain")]
    [InlineData("Demo.Domain.*")]
    [InlineData("*")]
    public void AcceptsWellFormedPatterns(string pattern)
        => Assert.True(ScopePattern.IsValid(pattern));
}
```

**Step 2:** red run (type missing).

**Step 3: implement** — `IsValid` (non-blank; either `*`, or no `*` at all, or exactly one trailing `.*`), and `Matches` (ordinal comparison; `*` ⇒ true; trailing `.*` ⇒ scope equals the prefix or starts with `prefix + "."`; otherwise exact equality). Ordinal, not case-insensitive: C# namespaces are case-sensitive.

**Step 4/5:** green; commit `feat: ScopePattern matcher for architecture rules`.

---

### Task 3: CheckArchitectureLogic (TDD — the core)

**Files:** Create `tests/RoslynCodeLens.Tests/CheckArchitectureLogicTests.cs`, `src/RoslynCodeLens/Tools/CheckArchitectureLogic.cs`.

Signature: `Execute(LoadedSolution loaded, SymbolResolver resolver, IReadOnlyList<ArchitectureRule> rules, string scope, int maxSitesPerViolation)` → `IReadOnlyList<ArchitectureViolation>`.

**Algorithm**

1. Validate: `rules` non-empty; each `Kind` is `forbid`/`allowOnly`; `To` non-empty; every pattern `ScopePattern.IsValid`; `scope` is `namespace`/`project`. Failures → `InvalidArgument` naming the rule index.
2. For each compilation, for each syntax tree: skip generated. Compute the document's candidate source scopes — for `scope: "namespace"`, the namespace names declared in the tree (`BaseNamespaceDeclarationSyntax`, covering both block and file-scoped forms; a tree with none is the global namespace, `""`); for `scope: "project"`, the project name. **If no candidate scope matches any rule's `From`, skip the tree entirely — before creating a semantic model.** This is the optimisation that keeps cost proportional to the rules.
3. Otherwise get the semantic model and walk descendant nodes. For each node, `model.GetSymbolInfo(node).Symbol` — take `ITypeSymbol` directly, otherwise the symbol's `ContainingType`; skip when neither. Derive:
   - `sourceScope`: for namespace scope, the nearest enclosing `BaseNamespaceDeclarationSyntax`'s name (else `""`); for project scope, the project name.
   - `targetScope`: for namespace scope, the type's `ContainingNamespace.ToDisplayString()` (empty for global); for project scope, the project owning the type's source location, else its containing assembly name.
   - `isInternal`: `type.Locations.Any(l => l.IsInSource)`.
4. Skip when `sourceScope == targetScope` (self-reference — never a violation, both kinds).
5. Evaluate each rule: `forbid` fires when `From` matches source and any `To` matches target. `allowOnly` fires when `From` matches source, the target `isInternal`, and no `To` matches target.
6. Accumulate per `(ruleIndex, sourceScope, targetScope)`: increment count, and append a `ViolationSite` while `Sites.Count < maxSitesPerViolation`.
7. Sort by rule index, then descending `ReferenceCount`.

**Tests** — fixture with two projects so project scope and cross-project edges are real:

```csharp
private static (LoadedSolution, SymbolResolver) Layered() => RenameTestWorkspace.Create(
    ("Domain", new[] { ("Order.cs", """
        namespace Demo.Domain;
        public class Order { public int Id; }
        """) }),
    ("Infra", new[] { ("Repo.cs", """
        namespace Demo.Infrastructure;
        public class Repo { public Demo.Domain.Order? Load() => null; }
        """) }));
```

1. `Forbid_Violated` — forbid `Demo.Infrastructure.*` → `Demo.Domain.*` ⇒ one violation, `SourceScope`/`TargetScope` correct, `ReferenceCount` ≥ 1, a site with a real file/line.
2. `Forbid_Satisfied` — forbid the opposite direction (`Demo.Domain.*` → `Demo.Infrastructure.*`) ⇒ empty.
3. `AllowOnly_CatchesUnlistedDependency` — allowOnly `Demo.Infrastructure.*` → `["Demo.Shared.*"]` ⇒ violation naming `Demo.Domain`.
4. `AllowOnly_SatisfiedWhenListed` — allowOnly `Demo.Infrastructure.*` → `["Demo.Domain.*"]` ⇒ empty.
5. `AllowOnly_IgnoresFrameworkTargets` — a source file using `System.Collections.Generic.List<int>` under an allowOnly rule that doesn't list `System.*` ⇒ **empty** (the design's key semantic).
6. `Forbid_CanTargetFrameworkNamespace` — forbid `Demo.Domain.*` → `System.Collections.Generic` on a file using `List<int>` ⇒ violation (the same target the previous test ignores).
7. `SelfReference_IsAllowed` — a type referencing another type in its own namespace, under both a `forbid Demo.Domain.* → Demo.Domain.*` and an `allowOnly` with an empty-ish `to` ⇒ empty in both.
8. `FullyQualifiedReference_WithNoUsing_IsDetected` — the fixture above deliberately writes `Demo.Domain.Order` fully qualified with no `using`. Assert the violation is still found. **This is the test justifying the semantic approach over the existing usings-based one — do not delete it.**
9. `Grouping_CountsAllReferencesButLimitsSites` — a file with 5 references across the boundary and `maxSitesPerViolation: 2` ⇒ one item, `ReferenceCount == 5`, `Sites.Count == 2`.
10. `ProjectScope_UsesProjectNames` — same fixture with `scope: "project"`, forbid `Infra` → `Domain` ⇒ violation with those scopes.
11. `GeneratedCode_IsSkipped` — a generated-looking tree contributing no violations.
12. `ExactPatternDoesNotMatchChildren` — forbid `Demo.Domain` (no `.*`) against a reference to `Demo.Domain.Orders.Thing` ⇒ empty; with `Demo.Domain.*` ⇒ violation.
13. Error cases: empty rules; unknown kind; `allowOnly` with empty `to`; malformed pattern; unknown scope — each `InvalidArgument` mentioning the rule index where applicable.
14. `Sorting_WorstFirstWithinRuleOrder`.

Commit `feat: check_architecture rule evaluation over the semantic type graph`.

---

### Task 4: Tool wrapper + fixture test

**Files:** Create `src/RoslynCodeLens/Tools/CheckArchitectureTool.cs`, `tests/RoslynCodeLens.Tests/CheckArchitectureFixtureTests.cs`.

Wrapper mirrors `FindReferencesTool`: `EnsureLoaded()`, `GetAnalysisContext()`, envelope with summary `{ byRule, totalReferences, rulesEvaluated }`. Parameters: `rules` (array of the model), `scope = "namespace"`, `maxSitesPerViolation = 5`, `limit`. The `[Description]` must state both semantics explicitly — `allowOnly` ignores framework targets, self-references are always allowed — because an agent that doesn't know them will misread an empty result.

Fixture test: a rule pair against the real TestSolution (e.g. forbid the test projects' namespace from depending on something it genuinely doesn't, expecting empty; and one that genuinely fires, e.g. `allowOnly` on `TestLib` with an implausible allow-list). Read the fixture's namespaces first rather than guessing.

Commit `feat: expose check_architecture MCP tool`.

---

### Task 5: Docs + verification

- SKILL.md: Red Flags row `| "Is anything violating our layering?" / "Does Domain reference Infrastructure?" | check_architecture |`; a bullet beside `find_circular_dependencies` covering both rule kinds, the two semantics, and grouping; Quick Reference row; metadata-support row (source scan; `forbid` may target metadata namespaces, `allowOnly` ignores them).
- CLAUDE.md: 64 → **65**.
- `tools/DocGen/Program.cs`: `["check_architecture"] = "code-quality"`.
- README.md: one bullet in the existing style.
- `docs/BACKLOG.md`: medium-tier bullet → ✅ shipped (PR #n) + Recently-shipped row.
- Full `dotnet build` + `dotnet test` (~950 expected; ~9 min — run in background). Fixture-pristine check.

Commit `docs: document check_architecture (65 tools)`.

---

## Deviations
Report anything about node-level symbol resolution that proved too broad or too narrow (the walk in step 3 is the part most likely to need tuning), the real cost on the TestSolution fixture, and any case where source-side filtering skipped a document that should have been analysed.
