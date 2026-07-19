# rename_symbol Review-Findings Fix Plan

Date: 2026-07-19. Fixes the 10 findings from the post-merge high-effort review of PR #298 (merged as 74b8024). Branch `fix/rename-symbol-review-findings`. Executed as four work packages; TDD throughout.

## Findings → fixes

| # | Finding | Fix | Package |
|---|---|---|---|
| 1 | Apply overwrites files from stale snapshot, no lock vs watcher rebuild | On-disk freshness precheck (snapshot text vs current disk text) before writing; abort with per-file conflict list on mismatch | W2 |
| 2 | `DiagnosticKey` Id\|Path\|Message wrong both directions | Count-based multiset diff keyed `(Id, FilePath)` — message drift irrelevant, count increase catches same-message-new-line | W1 |
| 3 | Degraded load → silent incomplete rename | Refuse apply when `loaded.Degraded` unless `force`; warn in preview `Message` | W2 |
| 4 | Non-atomic multi-file write; cancel can truncate a file | Write temp file + `File.Move(overwrite)` per doc; on any failure restore all already-written originals (captured up front) | W2 |
| 5 | Post-apply staleness ≥ debounce window; renamed Solution discarded | After successful write, commit the renamed solution in-memory: update documents in place (`WithDocumentText`) + refresh cached compilations via the manager, so immediate queries see new text | W2 |
| 6 | `Data.Repository` misses generic `Data.Repository<T>` | Secondary lookup keyed on generic-arity-stripped full name in `SymbolResolver.BuildTypeLookups`; dotted branch falls back to it (multiple arities → multiple matches → existing ambiguity path) | W3 |
| 7 | Conflict scan misses textually-unchanged downstream projects | Scan changed projects ∪ their direct dependents (via `solution.GetProjectDependencyGraph().GetProjectsThatDirectlyDependOnThisProject`) | W1 |
| 8 | Encoding/BOM rewritten (UTF-8 no BOM always) | Preserve original document's `SourceText.Encoding` when writing; fallback UTF-8-no-BOM only when null | W2 |
| 9 | Conflict check recompiles old projects, whole-compilation diagnostics, sequential | Use `LoadedSolution.Compilations` cache for the before side; `Task.WhenAll` old/new per project and across projects (bounded); align suppressed-diagnostic policy with `GetDiagnosticsLogic` (`IsSuppressed` skipped) | W1 |
| 10 | CLAUDE.md "57 tools" stale; BACKLOG in-flight entry stale | CLAUDE.md → 58; BACKLOG: move rename_symbol to Recently shipped (PR #298), drop in-flight entry, note this fix branch | W4 |

## Package details

### W1 — Conflict gate (`RenameSymbolLogic.ComputeConflictsAsync`)
- New algorithm per scanned project: `before` = cached compilation (fallback `GetCompilationAsync`), `after` = forked project compilation; both diagnostics passes concurrent. Errors only, `!IsSuppressed`.
- Multiset diff: group each side by `(Id, NormalizedPath)`; for groups where `afterCount > beforeCount`, report `afterCount - beforeCount` diagnostics from the after side (prefer ones whose line is not present in the before group).
- Scan set: `GetProjectChanges()` projects ∪ direct dependents of those (deduped). Dependents use whole-compilation diagnostics (their trees didn't change).
- Tests (AdhocWorkspace, two-project where needed): message-embeds-old-name no longer reported as conflict; same-Id-same-file-new-line collision IS reported; dependent-project break detected (multi-project workspace, project B referencing renamed A); suppressed diagnostics ignored.

### W2 — Write path (`SolutionChangeWriter`, `RenameSymbolLogic`, manager commit)
- `WriteChangesToDiskAsync` → returns a write report; per changed doc: read original doc's `SourceText` (encoding), verify current on-disk bytes/text still match the snapshot original (`File.ReadAllText` compare; missing file counts as mismatch) → collect mismatches and abort before writing anything if any; then write via temp file in same directory + `File.Move(temp, target, overwrite: true)`; on exception mid-loop restore every already-replaced file from its captured original text and rethrow with context.
- Encoding: `originalText.Encoding ?? changedText.Encoding ?? new UTF8Encoding(false)`.
- Post-write commit: new manager API (e.g. `SolutionManager.CommitDocumentTexts(IReadOnlyList<(DocumentId, SourceText)>)` exposed through `MultiSolutionManager`) that in-place-updates `_loaded.Solution`/`Compilations` under the same serialization the watcher rebuild uses (follow the #282/#288 in-place `WithDocumentText` pattern already in `SolutionManager`). rename_symbol calls it after a successful disk write; apply_code_action keeps current behavior (its window is small; wiring it up can follow).
- Degraded guard in `ExecuteAsync`: preview → prepend warning w/ `LoadDiagnostics` count to `Message`; apply without `force` → refuse (`Success=false`) telling the user why.
- Tests: freshness mismatch aborts without writing; failure mid-batch restores originals (simulate via read-only file); encoding preserved (UTF-8 BOM fixture file round-trips with BOM); post-commit immediate `FindSymbols`/references on the updated LoadedSolution sees the new name (unit-level via manager test seam); degraded refusal (LoadedSolution with LoadDiagnostics).
- Keep `apply_code_action` green (it shares the writer; its callers adapt to the new return/abort semantics — behavior for clean disk state must be unchanged).

### W3 — Resolver generic lookup (`SymbolResolver`)
- `BuildTypeLookups`: additional `Dictionary<string, List<INamedTypeSymbol>>` keyed by full display name with generic args stripped per segment (`Data.Repository<T>` → `Data.Repository`; nested `Outer<T>.Inner` → `Outer.Inner`). Dotted branch of `FindNamedTypes`: exact `_typesByFullName` first, then stripped-name dict.
- Tests: `FindSymbols("Data.Repository")` finds `Repository<T>`; two arities (`Repository<T>`, `Repository<T1,T2>`) → both returned (rename then reports AmbiguousMatch listing both); member lookup `Data.Repository.GetById` resolves; non-generic behavior unchanged.

### W4 — Docs + verification
- CLAUDE.md: "57" → "58 code intelligence tools".
- BACKLOG.md: remove in-flight entry; §5 bullet 🔧 → ✅ shipped PR #298; add row to Recently shipped table (`rename_symbol` | Refactoring | #298).
- SKILL.md: no change needed unless W1/W2 alters tool-visible behavior descriptions (degraded refusal + freshness abort → extend the rename_symbol bullet's one-liner).
- Full `dotnet build` + `dotnet test` green; fixture-pristine check.
