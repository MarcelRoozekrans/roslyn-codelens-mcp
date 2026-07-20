# `change_signature` — Design

Date: 2026-07-20
Status: Approved
Origin: SharpLensMcp gap analysis (docs/BACKLOG.md §5, medium tier). Add, remove, and reorder a method's parameters with every call site updated. Like `rename_symbol`, it is not reachable through `apply_code_action`. Tool count 63 → 64.

## Empirical findings (verified, not assumed)

A probe against Microsoft.CodeAnalysis 5.6 established the constraints that shape this design:

- **Every `ChangeSignature` type in Roslyn is internal** — `Microsoft.CodeAnalysis.Features` exports **zero** public `ChangeSignature` types, in direct contrast to `Renamer`, which is public in `Workspaces`. There is no supported public API.
- **`AbstractChangeSignatureService.ChangeSignature(SemanticDocument, ISymbol, SyntaxNode, SyntaxNode, SignatureChange, LineFormattingOptions, CancellationToken)` is a *public method on an internal type*** — reachable by reflection, and it takes a `SignatureChange` directly.
- **That entry point bypasses the UI.** `IChangeSignatureOptionsService` exists but is only consumed by the interactive `ChangeSignatureWithContextAsync` path (the IDE dialog). Driving `ChangeSignature(...)` needs no options service.
- **`ParameterConfiguration(ExistingParameter thisParameter, ImmutableArray<…> parametersWithoutDefaultValues, ImmutableArray<…> remainingEditableParameters, ExistingParameter paramsParameter, int selectedIndex)`** models the extension-method `this` parameter, the `params` array, and the default-value split as first-class slots.
- `SignatureChange(ParameterConfiguration original, ParameterConfiguration updated)`.

## Decisions (brainstorming outcomes)

| Question | Decision |
|---|---|
| Engine | **Reflection into Roslyn's internal service.** It already handles cascading to overrides and interface implementations, named and optional arguments, `params` arrays, extension-method `this`, and XML `<param>` docs. Hand-rolling that matrix is where a write tool ships silent corruption — every review this session found 2–4 real bugs in far simpler read-only analysis. |
| Operations | **remove + reorder + add.** |
| Added-parameter values | **Caller supplies `callSiteValue`, required.** No inference: the tool never guesses semantics at a call site. An optional `defaultValue` makes the parameter optional, letting existing call sites omit it — the genuinely safe form. |
| Safety | `rename_symbol`'s model — preview default, diagnostics-delta conflicts with `force`, degraded-load refusal, shared write path — **plus a `cascadedTo` report** naming every override/implementation Roslyn also rewrote, so the blast radius is visible before applying. |

## Tool contract

```
change_signature(
  method: string,
  operations: [
    { kind: "remove",  parameter: "logger" },
    { kind: "reorder", order: ["id", "name", "token"] },
    { kind: "add", name: "token", type: "System.Threading.CancellationToken",
      callSiteValue: "CancellationToken.None", defaultValue: "default" }
  ],
  preview: bool = true,
  force: bool = false)
```

Operations apply in order against the original parameter list. `reorder` takes a full permutation of the parameters surviving at that point (partial orders are rejected rather than guessed at). `add` appends unless the following `reorder` places it.

## Result — `ChangeSignatureResult`

`{ Success, Method (resolved display), OldSignature, NewSignature, Applied, Edits[], FilesChanged, CascadedTo[] (display strings of overrides/implementations also rewritten), Conflicts[] (RenameConflict shape), Message }`.

## Architecture

`Analysis/ChangeSignatureBridge.cs` isolates **every** reflection call behind one typed surface: resolve the internal types and members once (cached in a `Lazy`), build the original and updated `ParameterConfiguration` preserving Roslyn's `this`/`params`/default-value slots, construct `SignatureChange`, invoke `ChangeSignature(...)`, and return a plain `Solution`. No reflection leaks into the logic or tool layers.

**Failure mode is loud, never silent.** If any probe fails — a Roslyn upgrade renaming a type or changing a signature — the bridge throws `McpToolException(Internal, …)` naming the missing member *before anything is written*. A tool that rewrites call sites must never degrade gracefully into partial work.

`ChangeSignatureLogic` resolves the method (`SymbolResolver` → overload group ⇒ `AmbiguousMatch`), validates the operation list against the actual parameters, calls the bridge, computes conflicts and the cascade set, and returns edits. `ChangeSignatureTool` is the thin MCP wrapper passing `manager.CommitDocumentTextsAsync`.

## Safety pipeline (reusing shipped infrastructure)

1. Degraded load → refuse apply unless `force`; warn in preview.
2. Bridge probe → `Internal` error if the API moved.
3. Diagnostics delta (compiler errors, count-based per `(Id, FilePath)` as established in #300) → `Conflicts`; apply refuses unless `force`.
4. `SolutionChangeWriter.WriteChangesToDiskAsync` → freshness refusal, atomic writes with rollback, encoding preservation.
5. `SolutionChangeWriter.CommitAsync` → immediate snapshot update; no outcome may fail the operation.

## Errors

`SymbolNotFound` (method or parameter), `AmbiguousMatch` (overloads — the caller must qualify), `InvalidArgument` (empty operations, unknown `kind`, `reorder` that isn't a permutation, `add` without `callSiteValue`, removing a parameter that doesn't exist), `Internal` (bridge probe failure).

## Known limits

Static rewriting only: reflection-based and `dynamic` call sites are not updated, and neither are call sites in projects that failed to load. Partial methods and cross-project overrides follow Roslyn's own cascade rules. Delegates whose shape no longer matches are surfaced through the conflict check rather than rewritten.

## Testing

Matrix on `RenameTestWorkspace`: each operation alone and combined; named arguments at call sites; optional parameters; `params` arrays; extension-method `this`; the override and interface-implementation cascade (asserting `CascadedTo`); XML `<param>` doc updates; `add` with and without `defaultValue` (call sites updated vs left omitting); conflict detection when a removed parameter is still referenced in the body; every error case above; preview leaves disk untouched; apply writes and commits. Plus a **bridge-probe test** that fails loudly if the internal API moves — the early-warning system for a Roslyn upgrade. Fixture integration on TestSolution (preview only, so fixture files stay pristine).

## Docs

SKILL.md: Red Flags row ("add/remove/reorder a parameter" / "let me edit N call sites"), tool bullet next to `rename_symbol`, Quick Reference row, metadata-support row (source only). CLAUDE.md 63 → **64**. `tools/DocGen/Program.cs` categoryMap → `diagnostics` (alongside `rename_symbol`). README bullet. BACKLOG: mark shipped + Recently-shipped row.
