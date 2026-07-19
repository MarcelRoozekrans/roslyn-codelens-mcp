# `rename_symbol` — Design

Date: 2026-07-19
Status: Approved
Origin: SharpLensMcp gap analysis (docs/BACKLOG.md §5). Rename is not a Roslyn code action, so `apply_code_action` cannot do it; today an agent renaming a symbol falls back to multi-file text edits — the exact failure mode this server exists to prevent.

## Decisions (brainstorming outcomes)

| Question | Decision |
|---|---|
| Symbol addressing | Name-based, same contract as `find_references` / `analyze_method` (`MyClass`, `Namespace.MyClass`, `MyClass.Member`), resolved via the shared `SymbolResolver`. |
| Symbol kinds (v1) | Types + members (classes, interfaces, structs, records, enums, methods, properties, fields, events). Locals/parameters excluded — they need position addressing. |
| Rename options | Expose `renameOverloads` (default `true`), `renameInStrings` (default `false`), `renameInComments` (default `true`). `RenameFile` deferred. |
| Safety | Diagnostics-delta check: compare compiler error sets before/after the rename; new errors are reported as `Conflicts`. Apply mode refuses to write on conflicts unless `force=true`. Preview is the default. |
| Implementation | Roslyn `Renamer.RenameSymbolAsync` (Approach A). Rejected: manual `find_references`-based edit composition (reimplements ctor/`nameof`/`cref`/override cascade Roslyn already handles); external tooling (nothing suitable exists headless). |

## Tool contract

```
rename_symbol(
  symbol: string,            // "MyClass", "Namespace.MyClass", or "MyClass.Member"
  newName: string,           // bare identifier, e.g. "OrderProcessor"
  renameOverloads: bool = true,
  renameInStrings: bool = false,
  renameInComments: bool = true,
  preview: bool = true,      // like apply_code_action
  force: bool = false        // apply despite reported conflicts
)
```

Result — `RenameSymbolResult` (single-object shape, no list envelope, consistent with `apply_code_action`):

- `Success` — operation computed without error
- `OldName` — fully-qualified display string of the resolved symbol
- `NewName`
- `Applied` — whether files were written to disk
- `Edits` — `IReadOnlyList<TextEdit>` (existing model), per file
- `FilesChanged` — distinct file count
- `Conflicts` — new compiler errors introduced by the rename: `{ Id, Message, File, Line }`
- `Message` — human-readable status (e.g. refusal reason)

## Resolution & validation

Resolve via the shared `SymbolResolver` from `manager.GetAnalysisContext()`:

- Types by full name, then simple name; members via the member index — identical semantics to `find_references`.
- Multiple matches → `AmbiguousMatch` error with `details.matches` candidates.
- No match → `SymbolNotFound`.
- Symbol has no source location (metadata) → `InvalidArgument` ("cannot rename a metadata symbol").
- `newName` fails `SyntaxFacts.IsValidIdentifier` → `InvalidArgument`.
- Symbol resolves to a constructor → `InvalidArgument`, message directs the agent to rename the containing type (constructors follow the type automatically).

## Execution & safety

1. Take the current solution snapshot from the manager.
2. `Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(renameOverloads, renameInStrings, renameInComments, RenameFile: false), newName)`.
3. Diagnostics delta: for each project with changed documents, collect compiler errors (severity Error, **no analyzers** — cheap and deterministic) from the before and after compilations. Key by `Id + file + message`; errors present only in the after set become `Conflicts`. This compensates for the public Renamer API resolving conflicts best-effort without reporting the ones it couldn't.
4. `preview=true` → return edits + conflicts, disk untouched.
5. `preview=false` → if `Conflicts` non-empty and `force=false`, refuse (`Applied=false`, message explains `force`/preview options); otherwise write changed documents to disk.

## Write path & watcher interplay

Extract `ExtractTextEdits` and `WriteChangesToDiskAsync` from `CodeActionRunner` into a shared helper (`SolutionChangeWriter`) used by both `apply_code_action` and `rename_symbol` — no duplication, and the existing `apply_code_action` tests double as a regression net for the extraction.

Disk writes flow through the existing pipeline: file watcher → in-place `WithDocumentText` update (#282) → serialized rebuild. No new sync machinery. File renames are excluded in v1, so there is no delete/create watcher churn.

## Error handling

Existing error envelope (`isError: true`, `{ code, message, details? }`): `SymbolNotFound`, `AmbiguousMatch`, `InvalidArgument`, `Internal`. No new codes.

## Testing

xUnit against the existing TestSolution fixture:

- Rename a type — references, constructor, `nameof`, XML doc `cref` all cascade.
- Rename a method with overloads — `renameOverloads` on/off behavior.
- Rename a property; rename a field.
- `renameInComments` on/off; `renameInStrings` on/off.
- Collision rename (target name already exists in scope) — `Conflicts` populated; apply refused without `force`; `force=true` writes anyway.
- Ambiguous simple name → `AmbiguousMatch` with candidates.
- Unknown symbol → `SymbolNotFound`; metadata symbol → `InvalidArgument`; invalid identifier → `InvalidArgument`; constructor → `InvalidArgument`.
- `preview=true` leaves disk untouched; `preview=false` writes files matching the returned `Edits`.
- Existing `apply_code_action` suite passes after the `SolutionChangeWriter` extraction.

## Out of scope (v1) — backlog "deferred" candidates

- **File rename** (`Foo.cs` → `Bar.cs` via `RenameFile: true`) — delete+create watcher churn and git-mv semantics need their own design.
- **Locals / parameters** — require position-based addressing.
- **Cross-solution rename** — active solution only.
- **Analyzer-diagnostic delta** — compiler errors only in v1.
- **Overload-picking disambiguation** — v1 renames the whole overload group by default; a position override can come later if demand emerges.

## Documentation follow-ups

- SKILL.md: add `rename_symbol` to the write-side section (alongside `apply_code_action`), Red Flags table ("Let me edit N files to rename this symbol" → `rename_symbol`), Tool Quick Reference, and the metadata-support table (source-only).
- docs/BACKLOG.md: move `rename_symbol` from §5 to "In flight", then "Recently shipped" on merge.
