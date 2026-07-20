# Shared solution-scan walker — Design

Date: 2026-07-20
Status: Approved
Origin: `docs/BACKLOG.md` "Deferred from shipped features → From `check_architecture`". Three tools independently re-derive the same solution-wide scan, and they have **already drifted** — which is the argument for extracting it, not a hypothetical.

## The drift, measured

| | `find_throw_sites` | `find_catch_blocks` | `check_architecture` |
|---|---|---|---|
| Dedupe key | file path; **a pathless tree bypasses the dedupe entirely** | same | `(scope, identity)` with a content-hash fallback for pathless trees |
| Pre-model filter | none | none | source-side scope filter, applied *before* a semantic model is built |
| In-loop cancellation | no | no | every 1024 nodes |

Two consequences are live bugs in shipped tools, both fixed in `check_architecture` (PR #314) and still present in the other two:

1. **Pathless trees are counted once per compilation.** `if (!string.IsNullOrEmpty(tree.FilePath) && !walkedPaths.Add(...))` never adds a pathless tree to the set, so one present in several compilations is walked repeatedly and its sites double-count.
2. **Project attribution is a race.** The surviving row for a linked or multi-targeted file is credited to whichever compilation the dictionary happened to enumerate first, so `byProject` undercounts nondeterministically. (Flagged during the exception-flow review and accepted then; the walker makes fixing it free.)

## Shape

`Analysis/SolutionScanner.cs` exposes an iterator; callers keep their own node loops, so this replaces **enumeration only** and each tool's real logic is untouched.

```csharp
public sealed record ScanTree(
    ProjectId ProjectId, string ProjectName, Compilation Compilation,
    SyntaxTree Tree, SyntaxNode Root, Func<SemanticModel> SemanticModel);

public static IEnumerable<ScanTree> EnumerateTrees(
    LoadedSolution loaded, SymbolResolver resolver,
    Func<string, bool>? projectFilter = null,      // skip a whole compilation by project name
    Func<ScanTree, string>? scopeDiscriminator = null,   // extra dedupe dimension
    CancellationToken cancellationToken = default);
```

Responsibilities it owns: enumerate compilations; skip generated trees; dedupe robustly (tree identity, falling back to a content hash when `FilePath` is empty, optionally discriminated by caller scope); check cancellation per compilation and per tree.

**`SemanticModel` is a `Func`, deliberately.** `check_architecture` skips a tree after seeing its declared namespaces but before paying for a model, and that filtering is what keeps its cost proportional to the rules rather than to solution size. An eagerly-created model would silently undo it.

**The scope discriminator exists because "walk once" means different things per caller.** Under `check_architecture`'s project scope a linked file has a *different* source scope per compilation, so collapsing on path alone drops every violation after the first. Path-only callers pass nothing and get the current (correct, for them) behaviour.

## Migration

Each tool's loop becomes `foreach (var scan in SolutionScanner.EnumerateTrees(...))` with its existing body. `check_architecture` passes both the project filter and the scope discriminator; the other two pass neither and inherit the robust dedupe — which is precisely the two bug fixes propagating.

Per-node cancellation stays in the callers' loops (only they know their node cadence); the exception tools gain the periodic check `check_architecture` already has.

## Testing

Existing suites for all three tools are the regression net — behaviour must be identical except the two intended fixes. New tests: a pathless tree present in two compilations is walked once (was: once per compilation); project attribution for a linked file is deterministic; the scope discriminator still yields one entry per scope; the project filter skips a compilation before any tree work; cancellation is observed at compilation and tree level. `SolutionScanner` is pure enumeration, so it unit-tests directly against `RenameTestWorkspace`.

## Out of scope

The node-walking itself stays per tool — the three ask genuinely different questions of each node, and forcing a shared visitor would couple them without cause.
