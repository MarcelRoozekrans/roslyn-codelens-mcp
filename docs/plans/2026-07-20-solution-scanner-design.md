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
    Func<string, SyntaxTree, string>? scopeDiscriminator = null,   // extra dedupe dimension
    Func<Compilation, SyntaxTree, SemanticModel>? modelFactory = null,   // test seam: count models
    Func<SyntaxTree, CancellationToken, SyntaxNode>? rootFactory = null, // test seam: count parses
    CancellationToken cancellationToken = default);
```

Responsibilities it owns: enumerate compilations; skip generated trees; dedupe robustly (tree identity, falling back to a content hash when `FilePath` is empty, optionally discriminated by caller scope); check cancellation per compilation and per tree.

**Dedupe happens BEFORE the root is realised.** The key needs only the project name and the tree's identity — neither requires parsing — so a duplicate is dropped without ever calling `GetRoot`. Doing it the other way round costs a full parse per compilation a file appears in: a project multi-targeted across four frameworks would parse the same file four times and throw three away. This is why `scopeDiscriminator` takes `(projectName, tree)` rather than a `ScanTree`: at the moment the key is decided, no `ScanTree` exists yet.

**`SemanticModel` is a `Func`, deliberately.** `check_architecture` skips a tree after seeing its declared namespaces but before paying for a model, and that filtering is what keeps its cost proportional to the rules rather than to solution size. An eagerly-created model would silently undo it.

**And the `Func` is memoised.** Laziness is about not paying for trees that get filtered out, not about paying repeatedly for trees that don't: a caller reaching for the model in two places in its loop body wants one model. First call builds it; every later call on the same `ScanTree` returns that instance.

**The compilation ordering tiebreak must survive a reload.** Ordering by project name alone leaves same-named projects tied, and the tie is decided by a first-one-wins dedupe — so it must not be broken by `ProjectId.Id`, a Guid minted afresh on every workspace load. That is exactly as arbitrary as the `ConcurrentDictionary` order it was introduced to replace. The order is `(project name, .csproj path, assembly name, ProjectId.Id)`: the path is unique within a solution and stable across loads; the last two only matter for in-memory workspaces that have no path.

**The scope discriminator exists because "walk once" means different things per caller.** Under `check_architecture`'s project scope a linked file has a *different* source scope per compilation, so collapsing on path alone drops every violation after the first. Path-only callers pass nothing and get the current (correct, for them) behaviour.

## Migration

Each tool's loop becomes `foreach (var scan in SolutionScanner.EnumerateTrees(...))` with its existing body. `check_architecture` passes both the project filter and the scope discriminator; the other two pass neither and inherit the robust dedupe — which is precisely the two bug fixes propagating.

Per-node cancellation stays in the callers' loops (only they know their node cadence); the exception tools gain the periodic check `check_architecture` already has.

## Testing

Existing suites for all three tools are the regression net — behaviour must be identical except the two intended fixes. New tests: a pathless tree present in two compilations is walked once (was: once per compilation); project attribution for a linked file is deterministic; the scope discriminator still yields one entry per scope; the project filter skips a compilation before any tree work; cancellation is observed at compilation and tree level. `SolutionScanner` is pure enumeration, so it unit-tests directly against `RenameTestWorkspace`.

The performance invariants are counts, and a count is not observable from outside — hence the two factory parameters, which exist for tests and nothing else. They pin: enumeration builds **0** models, one accessor call builds 1, a second builds no more (`SemanticModel_IsCreatedOnlyWhenAsked_AndOnlyOnce`); a duplicated tree realises **1** root, not one per compilation (`DuplicateTree_IsNeverParsed`); and `check_architecture` over four trees where one matches a rule's `From` builds **exactly 1** model (`OnlyTreesMatchingARuleSource_GetASemanticModel`), which is the only test that fails if the pre-model filter is deleted — the behavioural tests all pass without it, since an unmatched tree yields no violation either way. `SemanticModelLazinessTests` stays: it catches a different failure, a scanning tool calling `GetSemanticModel` itself rather than the filter going missing.

## Out of scope

The node-walking itself stays per tool — the three ask genuinely different questions of each node, and forcing a shared visitor would couple them without cause.
