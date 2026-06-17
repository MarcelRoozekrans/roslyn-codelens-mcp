# Project-filter for `load_solution` — design

**Status:** approved (brainstorm 2026-06-17)
**Issue:** [#232](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/232)
**Theme:** Startup & loading performance (BACKLOG section 4)

## Problem

`load_solution(path)` opens an entire `.sln` via `MSBuildWorkspace.OpenSolutionAsync`. On large solutions (400+ projects) this blocks the calling agent for ~10 minutes; the MCP client typically times out at 30s before the first useful response.

Today's `MultiSolutionManager` lets users load multiple separate `.sln` files and switch between them — that's *solution-level* selectivity. There is no *project-level* selectivity within a single `.sln`, so users cannot say "from `huge.sln`, load only the projects I care about."

This design adds that capability.

## Out of scope

Two adjacent improvements were considered and **deferred** to the BACKLOG (see `docs/BACKLOG.md` section 4):

- **(b) Parallelise the per-project fallback loader** — `SolutionLoader.OpenPerProjectAsync` is currently sequential.
- **(c) Async `load_solution` with a load handle** — return immediately, let agents poll.

Both are revisited once this feature ships and we have measurements.

## API surface

`load_solution` gains two optional parameters; default behaviour unchanged.

```jsonc
{
  "path": "/abs/path/huge.sln",          // existing, required
  "include": ["MyApp.*", "Shared.Core"], // optional, glob array — matches Project.Name
  "rootProjects": ["MyApp.Api"]          // optional, exact-name array
}
```

Semantics:

- Both arrays act as **seeds**.
- Final loaded set = transitive closure (over `ProjectReference`) of `(glob-matched projects ∪ explicitly-named root projects)`.
- If neither is provided, full-solution load (existing behaviour, unchanged).
- If both are provided, their seed sets union before walking.
- If a `path` is already loaded under a different filter, the previous workspace is **disposed** before loading the new filter (replace semantics). Lets one logical `.sln` have only one view at a time; explicit `unload_solution` is required for memory-tight scenarios.

Tool description (agent-visible) adds: *"Pass `include` or `rootProjects` to load only a subset; the loader walks ProjectReference transitively to keep the workspace semantically complete."*

Return value adds informational fields:

```jsonc
{
  "loadedProjects": 23,
  "skipped": 377
}
```

## Internal flow

```
LoadSolutionTool.Execute(path, include?, rootProjects?)
  → MultiSolutionManager.LoadSolutionAsync(path, filter?)
     ↓
     If path already loaded → dispose old workspace (replace)
     ↓
     SolutionManager.CreateAsync(path, filter?)
        ↓
        SolutionLoader.OpenAsync(path, filter?)
           ↓
           ProjectClassifier.EnumerateProjects(path)   // all .sln entries
           ↓
           if filter is null:
               workspace.OpenSolutionAsync(...)        // existing path, unchanged
           else:
               1. Match seeds:
                    glob-match include against Project.Name
                    exact-match rootProjects against Project.Name
               2. Build per-project ProjectReference graph (XML parse of .csproj)
               3. BFS from seed set → closure set
               4. foreach project ∈ closure:
                    workspace.OpenProjectAsync(projectPath)   // sequential for now
               5. Return Solution from workspace + skipped = (all – closure)
```

Step 2 reads each `.csproj`'s `<ProjectReference>` elements via a lightweight XML parse — no full MSBuild evaluation. ~1ms per project file, so the graph walk is cheap even on 400-project solutions.

The filter passes through `MultiSolutionManager → SolutionManager → SolutionLoader`. No persistent state on the manager beyond the existing per-path cache; the filter is consumed once at load time and the resulting `Solution` is what every downstream tool sees.

## Error handling & edge cases

| Case | Behaviour |
|---|---|
| Both `include` and `rootProjects` provided, seeds = ∅ | Throw `InvalidArgument` with hint listing available project names |
| Invalid glob pattern (e.g. unclosed bracket) | Throw `InvalidArgument` naming the offending pattern |
| `rootProjects` names a non-existent project | Throw `InvalidArgument` listing all missing names |
| `ProjectReference` cycle | BFS visited-set terminates naturally; no special handling |
| Seed project is legacy/missing (classifier-tagged) | Included in closure pass, then per-project fallback drops it via existing `SkippedProject` channel |
| Seed has `ProjectReference` to legacy/missing | Closure walk includes if SDK-style, else `SkippedProject` with reason |
| `workspace.OpenProjectAsync` fails on a closure-included project | Existing fallback behaviour: log, add to `SkippedProject`, continue |

## Testing

Three layers, all xUnit in `tests/RoslynCodeLens.Tests/`.

### 1. Filter resolution — pure graph walk against in-memory project graph fixture

- `Closure_FromGlobSeeds_IncludesTransitiveDeps`
- `Closure_FromRootProjects_IncludesTransitiveDeps`
- `Closure_FromBoth_IsUnion`
- `Closure_StopsAtCycles`
- `Closure_EmptySeedSet_Throws`
- `Closure_UnknownRootProject_ThrowsListingMissing`
- `Closure_InvalidGlob_Throws`

### 2. Integration with `SolutionLoader` — real `.sln` fixture

New fixture at `tests/Fixtures/FilterableSolution/` with ~6 projects:

- `App.Api`
- `App.Domain`
- `App.Infrastructure`
- `Shared.Common`
- `Sample.Unrelated`
- `App.Api.Tests`

Verifies:

- `OpenAsync(path, filter: include=["App.*"])` loads `App.*` + `Shared.Common` (transitive), skips `Sample.Unrelated`.
- `OpenAsync(path, filter: rootProjects=["App.Api"])` loads `App.Api` + `App.Domain` + `App.Infrastructure` + `Shared.Common`, skips `Sample.Unrelated` and `App.Api.Tests`.
- Returned `Solution.Projects.Count` matches the closure size.
- `SkippedProject` list contains the excluded ones with a clear reason.

### 3. Tool-layer end-to-end — exercises `LoadSolutionTool.Execute`

- `Execute_WithInclude_ReturnsLoadedProjectsCount`
- `Execute_NoFilter_BehavesAsBefore` (regression gate)
- `Execute_RepeatedCallWithDifferentFilter_ReplacesPreviousLoad` (replace-semantics contract)

No new perf benchmark in this PR — benchmarks belong with the parallelisation backlog item (b), which is the natural next step once the filter ships and we have a baseline.
