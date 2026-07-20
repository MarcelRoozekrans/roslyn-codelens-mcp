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

1. **`allowOnly` considers only solution-internal targets.** Otherwise `using System;` violates "Api may depend only on Application", which is absurd and would bury every real finding. To restrict a framework namespace, write an explicit `forbid` — that path *does* evaluate metadata targets.
2. **Self-references are always allowed.** A scope depending on itself is not a layering violation, under either rule kind.

A `forbid` fires when the source scope matches `from` **and** the target matches `to`. An `allowOnly` fires when the source matches `from`, the target is solution-internal, is not the source scope itself, and matches none of the `to` patterns.

## Edge derivation

For each source document (generated code skipped, matching the sibling scan tools): determine its scope — the enclosing namespace declaration, or the project name when `scope: "project"`. **Source-side filtering happens first**: if that scope matches no rule's `from`, the document is skipped before any symbol resolution, so cost tracks the rules written rather than solution size.

For surviving documents, walk the syntax nodes that can bind to a type, resolve each through the semantic model, and derive the target scope from the resolved symbol's containing namespace (or its project/assembly for `scope: "project"`). Each `(rule, sourceScope, targetScope)` triple accumulates a count and its first sites.

## Result

Standard list envelope. `ArchitectureViolation { RuleKind, RuleDescription?, FromPattern, ToPattern, SourceScope, TargetScope, ReferenceCount, Sites[] }` where `Sites[] = { File, Line, Column, SourceSymbol, TargetSymbol }`. Summary: `{ byRule: { pattern: count }, totalReferences, rulesEvaluated }`.

Sorted by rule order, then by descending reference count — the worst breach of the first rule you wrote appears first.

## Errors

Empty `rules` → `InvalidArgument`. Unknown `kind`, `allowOnly` without a non-empty `to`, `forbid` without `to`, or a malformed pattern (e.g. an interior `*`) → `InvalidArgument` naming the offending rule index. Unknown `scope` → `InvalidArgument`. `EnsureLoaded()` as usual.

## Known limits

Static analysis: reflection- and `dynamic`-mediated dependencies are invisible, as are dependencies expressed only in configuration or DI registration by string. A type referenced only inside a lambda or local function still counts — it is a real dependency of the containing document. `scope: "project"` compares project names, so two projects sharing a name in different solution folders are indistinguishable.

## Testing

Matrix on `RenameTestWorkspace` (multi-project, since project scope and cross-project edges matter): `forbid` violated and satisfied; `allowOnly` catching an unlisted dependency; `allowOnly` ignoring a BCL target while `forbid` catches the same one; self-reference allowed under both kinds; exact vs prefix-wildcard vs `*` patterns; grouping (many references → one item with the right count) and `maxSitesPerViolation` truncation; `scope: "project"`; generated code skipped; every error case; and a fully-qualified reference with no `using` — the case the existing usings-based approach would miss, which is the reason this tool is semantic. Fixture integration on TestSolution.

## Docs

SKILL.md: Red Flags row ("is anything violating our layering?" / "does Domain reference Infrastructure?"), tool bullet near `find_circular_dependencies`, Quick Reference row, metadata-support row (source scan; `forbid` may target metadata namespaces). CLAUDE.md 64 → **65**. DocGen categoryMap → `code-quality` (with `find_circular_dependencies`). README bullet. BACKLOG: shipped + Recently-shipped row.
