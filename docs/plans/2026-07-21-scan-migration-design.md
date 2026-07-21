# Migrating the remaining scan tools onto `SolutionScanner` — Design

Date: 2026-07-21
Status: Approved
Origin: follow-up recorded in PR #316. Extends the already-approved scanner design (`docs/plans/2026-07-20-solution-scanner-design.md`) to the four tools that still hand-roll the loop.

## What the four actually do — measured, not assumed

The #316 follow-up note said these tools "carry the same two bugs". Reading them says otherwise, and the difference changes the migration:

| Tool | Generated trees | Dedupe |
|---|---|---|
| `find_async_violations` | skipped | **none** |
| `find_disposable_misuse` | skipped | **none** |
| `find_obsolete_usage` | **included**, flagged `IsGenerated` on each result | result-level `(file, span)` |
| `find_event_subscribers` | **included**, flagged `IsGenerated` on each result | result-level `(file, span)` |

Two consequences:

1. **`find_async_violations` and `find_disposable_misuse` have no dedupe at all**, so they double-count *every* tree that appears in more than one compilation — not just pathless ones. A project multi-targeting `net8.0;net9.0` opens as two compilations over the same files, so every violation is reported twice. This is a visible wrong count, and it is the reason this migration is worth doing now.
2. **`find_obsolete_usage` and `find_event_subscribers` deliberately include generated code.** That is a defensible choice, not an oversight: a deprecated API called from generated code still blocks a migration, and a generated `+=` can still leak. They surface it via `IsGenerated` so the caller decides. **Migrating them onto a scanner that unconditionally skips generated trees would silently delete results** — the naive-migration trap this design exists to avoid.

## The one scanner change

`EnumerateTrees` gains `bool includeGenerated = false`. Default preserves the behaviour of the three tools already migrated; the two flag-don't-skip tools pass `true`.

Nothing else about the scanner changes. Dedupe still happens before the root is realised, the semantic-model accessor stays lazy and memoised, and ordering stays `(project name, .csproj path, assembly name, id)`.

## Per-tool migration

- **`find_async_violations`, `find_disposable_misuse`** — straight swap; they gain tree-level dedupe. **This is a behaviour change and the point of the exercise**: counts drop to the truth on solutions with linked or multi-targeted files.
- **`find_obsolete_usage`, `find_event_subscribers`** — swap with `includeGenerated: true`. They gain tree-level dedupe (they currently walk a duplicated tree once per compilation and discard the repeats at result level), so the win is wasted work, not correctness. **Their result-level `(file, span)` dedupe stays.** It guards a different thing — the same span being reached twice within one walk — and removing it is not part of this change.

## Testing

The existing suites for all four are the regression net. New tests:

- A tree present in two compilations yields **one** violation from `find_async_violations` and one from `find_disposable_misuse` (both currently report two). This is the bug fix, so it must fail before the migration.
- `find_obsolete_usage` and `find_event_subscribers` **still report usages inside generated code**, with `IsGenerated: true`. This must pass before and after — it is the regression guard on the trap above.
- A model-count test per migrated tool is not warranted: unlike `check_architecture` none of these filters trees before binding, so there is no laziness invariant to protect beyond what the scanner already pins.

## Out of scope

The node-walking in each tool is untouched. `find_obsolete_usage`'s and `find_event_subscribers`' result-level dedupe stays as-is.
