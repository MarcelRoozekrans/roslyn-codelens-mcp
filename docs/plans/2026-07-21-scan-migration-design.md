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

## Outcome: two of the four migrated, two deliberately reverted

`find_async_violations` and `find_disposable_misuse` shipped. `find_obsolete_usage` and
`find_event_subscribers` were migrated, reviewed, fixed, reviewed again — and then **reverted to
their hand-rolled loops**, because the migration made them worse.

The reasoning matters more than the outcome. Those two are *correct* on main: walking every tree
under every compilation means symbol identity always finds a match on some pass. Deduping breaks
that, and every repair traded one defect for another:

| Attempt | Result |
|---|---|
| Migrate as-is | Usage vanished on 9/20 runs (obsolete), 11/20 (events) — nondeterministic, invisible |
| Match by fully-qualified name instead | Fixed the vanishing; introduced false positives — a `ref` overload's key collides with its non-`ref` sibling, and an FQN carries no assembly identity, so two unrelated projects declaring `Demo.Api.Legacy()` merge into one group and a *non-obsolete* method's calls get reported under another project's deprecation message |

A name is not an identity and a symbol is not portable; for these two tools there is no cheap key
that is both. Solving it properly means either indexing every compilation's view of a symbol, or
confirming at the usage site that the resolved symbol itself carries the attribute — a design
change, not a migration. **Trading correctness for deduplication in tools that are currently correct
is the wrong direction**, so they stay as they are until that work is done deliberately.

`includeGenerated` was removed with them: it had no remaining caller, and the eventual fix may not
take this shape.

## What the migration actually cost — read this before migrating the next tool

The section above framed the risk as "the scanner skips generated trees". That was the *visible*
trap. The one that shipped bugs is subtler, and every remaining hand-rolled tool will meet it.

**Tree-level dedupe means a tree present in N compilations is now bound under exactly ONE of them.**
The hand-rolled loops walked every tree under every compilation. Any state a tool carried per
compilation, and any symbol it obtained from outside the loop, silently assumed the opposite.

Two distinct failure shapes came out of that, both of which reached review:

### 1. Cross-compilation symbol identity

`find_obsolete_usage` built its target set from `SymbolResolver` and matched usages with
`SymbolEqualityComparer`. But `SymbolResolver` dedupes types by display name across compilations and
keeps whichever one reached the type first — a `ConcurrentDictionary` walk, so an arbitrary choice
that **differs between runs**. The scanner, by design, binds each tree under the one compilation it
deterministically picked. A symbol from compilation A is never `SymbolEqualityComparer`-equal to the
same declaration seen through compilation B, so whenever the two disagreed the usage vanished —
9 of 20 runs reported zero on a two-project solution sharing a linked declaration and use.
`find_event_subscribers` had the identical defect via `IsEventMatch` (11 of 20).

Before the migration these tools happened to survive because they saw the tree under *every*
compilation, so one of the passes always agreed with the resolver.

**Rule: never compare a symbol obtained outside the scan loop with one bound inside it by identity.**
Compare fully-qualified display names. `SymbolKeys.Fqn` is the one spelling — the convention the
exception-flow tools (PR #309) arrived at independently, generalised to members with parameter types
so two overloads cannot share a key. `ExceptionQueries.Fqn` delegates to it.

This applies to `FindImplementationForInterfaceMember` and friends too: they take a symbol of the
*same* compilation and quietly return null for one from another.

### 2. Per-compilation guards that became per-tree

`find_disposable_misuse` opened with `if (idisposable is null && iasyncDisposable is null) continue;`
— a guard on the *compilation*, cheap and correct when it ran once per compilation. Moved verbatim
into the per-tree loop it became actively harmful: the scanner has already awarded that tree's
first-one-wins dedupe slot to the compilation being skipped, so the tree is lost from every other
compilation holding it. A project whose references failed to load — a real MSBuildWorkspace failure
mode this repo has hit — silently deletes a shared file's findings from the healthy project too.
`find_async_violations` had the same structure around `taskSymbol is null`.

**Rule: a `continue` whose condition depends only on the compilation belongs in `projectFilter`, not
in the loop body.** That is what the parameter is documented to be for. Pre-compute the excluded
`ProjectId` set in one pass over `loaded.Compilations` and add it to the filter. The same argument
already applied to the test-project exclusion; the well-known-symbol guards are the same shape and
were simply missed.

### The general test that catches both

Neither defect shows up on a single-project solution, and the first one passes ~half the time on any
single run. A migration test needs **two projects sharing a file path** — and, for anything touching
resolver-provided symbols, **repeated iterations over independently built workspaces**, because the
nondeterminism is fixed at resolver-construction time and one workspace freezes one outcome.

## Out of scope

The node-walking in each tool is untouched. `find_obsolete_usage`'s and `find_event_subscribers`' result-level dedupe stays as-is.
