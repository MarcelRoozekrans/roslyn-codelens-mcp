# `check_architecture` — Design

Date: 2026-07-20
Status: Approved
Origin: SharpLensMcp gap analysis (docs/BACKLOG.md §5, medium tier). Enforce user-defined namespace/project dependency rules over the semantic type graph. `find_circular_dependencies` catches cycles; layering violations ("Domain must not reference Infrastructure") go undetected today. Tool count 64 → 65.

## Decisions (brainstorming outcomes)

| Question | Decision |
|---|---|
| Rule source | **Inline `rules` parameter.** Agent-native, no new file format to own (discovery, versioning, parse errors). A repo that wants durable policy stores rules in its own file and the agent passes them. |
| Edge derivation | **Semantic type references**, not `using` directives. `find_circular_dependencies` uses usings, which both over-report (an unused using) and under-report (a fully-qualified reference) — both failures silent. A tool whose output drives architectural decisions cannot make false accusations. |
| Rule kinds | **`forbid` + `allowOnly`.** `forbid` catches the violation you already know about; `allowOnly` catches the dependencies you haven't thought of, which is what actually prevents drift. |
| Output | **Grouped per violated (rule, sourceScope → targetScope) edge**, with a reference count and the first `maxSitesPerViolation` sites. One stray cross-boundary reference is one actionable item, not hundreds of lines. |

## Tool contract

```
check_architecture(
  rules: [
    { kind: "forbid",    from: "Demo.Domain.*", to: "Demo.Infrastructure.*", description?: "layering" },
    { kind: "allowOnly", from: "Demo.Api.*",    to: ["Demo.Application.*", "Demo.Domain.*"] }
  ],
  scope: "namespace" | "project" = "namespace",
  maxSitesPerViolation: int = 5,
  limit?: int)
```

`forbid.to` is a single pattern; `allowOnly.to` is the permitted set. Patterns are exact (`Demo.Domain`) or prefix-wildcard (`Demo.Domain.*` — matches that scope and everything beneath it). `*` alone matches everything.

## Semantics — the two rules that decide whether this tool is usable

1. **`allowOnly` considers only targets the caller can act on: solution-internal and not pure generator output.** Otherwise `using System;` violates "Api may depend only on Application", which is absurd and would bury every real finding; likewise generator output (regex implementations, JSON contexts, DI registries) would be reported as an unlisted dependency nobody can remove. Its target set is open-ended, so it needs the noise suppression. To restrict a framework *or* generated namespace, write an explicit `forbid` — that path names its target and *does* evaluate metadata and generated ones.
2. **Self-references are always allowed.** A scope depending on itself is not a layering violation, under either rule kind.

A `forbid` fires when the source scope matches `from` **and** the target matches `to`. An `allowOnly` fires when the source matches `from`, the target is solution-internal and not generated-only, is not the source scope itself, and matches none of the `to` patterns.

**"Solution-internal" is a property of the solution, not of how it loaded.** A target counts as internal when its containing assembly name matches one of the solution's own project or assembly names, falling back to a source-location check. Asking the symbol whether it has a source location answers the wrong question: when a ProjectReference resolves as a metadata reference instead — a real MSBuildWorkspace failure mode — every `allowOnly` rule over that boundary would silently return empty, indistinguishable from clean architecture.

## Edge derivation

For each source document (generated code skipped, matching the sibling scan tools): determine its scope — the enclosing namespace declaration, or the project name when `scope: "project"`. **Source-side filtering happens first**: if that scope matches no rule's `from`, the document is skipped before any symbol resolution, so cost tracks the rules written rather than solution size.

For surviving documents, walk the syntax nodes that can bind to a type, resolve each through the semantic model, and derive the target scope from the resolved symbol's containing namespace (or its project/assembly for `scope: "project"`). Each `(rule, sourceScope, targetScope)` triple accumulates a count and its first sites.

**The counting standard is: references a human reading the code would point at.** Three node kinds are deliberately **not** counted, because counting them reports dependencies nobody wrote:

* **`using` directives.** A using is not a dependency, it is a name-resolution convenience — the code beneath it is the dependency. Counting it also attributes a file-level using to the *global* namespace and double-counts an in-namespace using or alias. Skipping them is what makes the promise "an unused `using` is not reported" true.
* **`var`.** It is a `SimpleNameSyntax` whose `GetSymbolInfo` returns the *inferred* type, so `var o = new Demo.Domain.Order(); return o.Id;` is **two** dependencies on `Order` — exactly what the explicitly-typed form yields.
* **Initializer member names.** A member resolves to its `ContainingType`, so `new Demo.Domain.Order { Id = 1, Name = "x" }` would count `Order` three times — once for the type the reader actually wrote, once per assigned member. When the assigned member's containing type *is* the type the initializer initializes, the member is dropped; the count is **one**. This covers `new T { … }`, `new() { … }`, `x with { … }` and the nested `P = { … }` form. Assignment through a local (`other.Id = 1`) is *not* an initializer and still counts — it is a separately written reference. Consequence, accepted for the same reason as `var`: in `new Box { Inner = { Id = 1 } }` the type of `Inner` is never written, so it is not counted.

**Deduping the walk is scope-aware.** A linked or multi-targeted file appears in several compilations and must be walked once — but "once" means something different per scope. Under `namespace` scope its source scope is identical in every compilation, so the key is the file path: walking it twice would double-count one written reference. Under `project` scope its source scope *differs* per compilation, so the key is `(project name, path)`: a path-only key would credit the file to whichever project was enumerated first and silently drop every violation the other project's copy produces. Multi-targeting still collapses, since its several compilations share one project name. A tree with no file path falls back to a hash of its own text, so it dedupes too rather than being counted once per compilation.

## Result

Standard list envelope. `ArchitectureViolation { RuleIndex, RuleKind, RuleDescription?, FromPattern, ToPattern, SourceScope, TargetScope, ReferenceCount, Sites[] }` where `Sites[] = { File, Line, Column, SourceSymbol, TargetSymbol }`. `RuleIndex` is the position in the caller's own `rules` array, so a violation is always traceable back to the rule as written. Summary: `{ byRule: { rule: count }, totalReferences, rulesEvaluated }`, where `byRule` is keyed by the **rule as written** (index + kind + from + full to-set) — one written rule is always one bucket, even when it has several `to` patterns and several of them are violated.

Sorted by rule order, then by descending reference count — the worst breach of the first rule you wrote appears first.

## Errors

Empty `rules` → `InvalidArgument`. Unknown `kind`, `allowOnly` without a non-empty `to`, `forbid` without `to`, or a malformed pattern (e.g. an interior `*`) → `InvalidArgument` naming the offending rule index. Unknown `scope` → `InvalidArgument`. A `maxSitesPerViolation` below 1 → `InvalidArgument`: a violation with no sites is an accusation with no location. `EnsureLoaded()` as usual.

## Known limits

Static analysis: reflection- and `dynamic`-mediated dependencies are invisible, as are dependencies expressed only in configuration or DI registration by string. A type referenced only inside a lambda or local function still counts — it is a real dependency of the containing document. `scope: "project"` compares project names, so two projects sharing a name in different solution folders are indistinguishable.

Under `scope: "project"` the *target* scope falls back to the containing assembly name when the owning project can't be identified — the same load-dependence that the internal/external check above deliberately avoids. In practice an assembly name matches its project name, so a rule keeps matching; but a project deliberately renamed away from its assembly could be missed by a `forbid` written against the project name if its reference resolves as metadata. Namespace scope, the default, is unaffected.

## Testing

Every negative test carries a positive control on the same fixture — a rule that DOES fire — so a walk that silently found nothing cannot pass as clean architecture. Verified by mutation: stubbing `Execute` to return an empty list fails every test that asserts on its output.

Matrix on `RenameTestWorkspace` (multi-project, since project scope and cross-project edges matter): `forbid` violated and satisfied; `allowOnly` catching an unlisted dependency; `allowOnly` ignoring a BCL target while `forbid` catches the same one; self-reference allowed under both kinds; exact vs prefix-wildcard vs `*` patterns; grouping (many references → one item with the right count) and `maxSitesPerViolation` truncation; `scope: "project"`; generated code skipped; every error case; and a fully-qualified reference with no `using` — the case the existing usings-based approach would miss, which is the reason this tool is semantic. Fixture integration on TestSolution.

## Docs

SKILL.md: Red Flags row ("is anything violating our layering?" / "does Domain reference Infrastructure?"), tool bullet near `find_circular_dependencies`, Quick Reference row, metadata-support row (source scan; `forbid` may target metadata namespaces). CLAUDE.md 64 → **65**. DocGen categoryMap → `code-quality` (with `find_circular_dependencies`). README bullet. BACKLOG: shipped + Recently-shipped row.
