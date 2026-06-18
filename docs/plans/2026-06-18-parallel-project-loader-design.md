# Parallel project loader — design

**Status:** approved (2026-06-18)
**Issue:** [#232](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/232) — deferred item (a)
**Theme:** Startup & loading performance (BACKLOG section 4)
**Builds on:** [project-filter for `load_solution`](2026-06-17-project-filter-load-solution-design.md)

## Problem

`SolutionLoader.OpenPerProjectAsync` — the path used for the legacy-project fallback and for every filtered load — opens projects one at a time:

```csharp
foreach (var entry in classified)
    await workspace.OpenProjectAsync(entry.Path);
```

On a 400-project solution this is the dominant wall-clock cost even after filtering. Each `OpenProjectAsync` runs a full MSBuild design-time build, and Roslyn 5.x runs those builds **out-of-process in a `BuildHost` worker** (note the `BuildHost-netcore` / `BuildHost-net472` folders shipped alongside the assembly). A single `MSBuildWorkspace` owns a single `BuildHost`, so the builds it dispatches are effectively serialised regardless of how we await them.

## What the experiment showed

Measured against the `FilterableSolution` fixture (6 projects, 20-core box):

| Strategy | Wall-clock | Result | Verdict |
|---|---|---|---|
| Sequential, one shared workspace (today) | 10.4 s | 6 projects ✓ | correct, slow |
| **Parallel** `OpenProjectAsync` on **one shared** workspace | 6.1 s | **16 projects** ✗ | **corrupts the solution** |
| Parallel, **one workspace per project** | 5.3 s | each correct | fastest, but redundant work |

Two findings drive the design:

1. **Concurrent `OpenProjectAsync` on a shared workspace is unsafe.** The races between transitive loads produce duplicate `Project` entries (16 instead of 6). This is the documented "`MSBuildWorkspace` is not thread-safe for opens" hazard. Ruled out.
2. **One `BuildHost` per workspace is the only way to get real build parallelism.** Isolated workspaces hit the wall-clock floor. The cost: opening project `A` transitively loads `A`'s dependencies *into A's workspace too*, so naïvely opening all N projects in N workspaces does far more than N builds (the fixture did 16 builds for 6 projects).

## Approach: bounded pool of isolated workspaces + global path dedup + re-stitch

```
1. Assign a fresh ProjectId per target project path.
2. Order targets roots-first (descending transitive-closure size).
3. Bounded pool (degree = ROSLYN_CODELENS_LOAD_PARALLELISM, default min(CPU, 8)):
     for each target not already captured:
        open it in its OWN MSBuildWorkspace (own BuildHost)
        capture EVERY in-set project that workspace loaded (the project + its
          transitive deps) into a global ConcurrentDictionary<path, Project>,
          keep-first-wins  ← curbs the redundant-build blow-up
4. Re-stitch: rebuild each captured Project as a ProjectInfo into ONE fresh
   AdhocWorkspace, rewiring ProjectReferences from the on-disk graph
   (ProjectGraphReader, already used by the filter feature) mapped to the
   ProjectIds assigned in step 1.
5. Return that workspace's Solution + the skipped list.
```

### Why dedup curbs the blow-up

The filter closure is *transitively closed*: an in-closure project only references other in-closure projects. So when a worker opens a "root" it transitively pulls its whole closure into that worker's workspace — and we capture all of them at once. Roots-first ordering means a few big opens populate the dictionary, and the many leaf projects are skipped before they are ever scheduled. Worst case (every project a root) degrades to the per-workspace numbers above; typical case approaches one build per project.

### Why re-stitch is safe

- The returned workspace is **discarded by every caller** (`(solution, _, skipped) = …`); downstream code consumes only `Solution` + `Compilations`. `LoadedSolution.Empty` already uses an `AdhocWorkspace`, so an adhoc-backed `Solution` is a supported shape.
- Each captured `Project` keeps its own `ProjectId`/`DocumentId`s (globally-unique GUIDs), so cross-workspace id collisions can't happen — we dedup by **file path**, keeping one `Project` per path.
- `Project.MetadataReferences` on an MSBuild-loaded project holds only *external* references (framework / NuGet); an in-set project reference is a `ProjectReference`, never a metadata one. We strip every `ProjectReference` (its ids are workspace-local and meaningless after re-stitch) and rebuild them from the on-disk graph → the only cross-project wiring we touch, and it's path-based so it's deterministic and cross-platform (reusing `ProjectGraphReader`'s separator normalisation).

### Degree of parallelism

`ROSLYN_CODELENS_LOAD_PARALLELISM` (int, > 0). Default `min(Environment.ProcessorCount, 8)` — each worker is a separate `BuildHost` **process**, so an unbounded fan-out on a 400-project solution would spawn dozens of processes and exhaust memory. `1` forces single-worker loading (deterministic; used by CI/tests and as an escape hatch). Mirrors the existing `ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS` convention.

## Error handling & edge cases

| Case | Behaviour |
|---|---|
| A worker's `OpenProjectAsync` throws | Record `SkippedProject(Failed)`, continue (unchanged semantics, now thread-safe) |
| A worker times out (`RunWithTimeoutAsync` → null) | Record `SkippedProject(Timeout)`, continue |
| User cancellation (outer `ct`) | Propagates `OperationCanceledException` out of the pool |
| Non-SDK (legacy/missing/unknown) entry | Skipped up front, never scheduled (unchanged) |
| In-set project references an out-of-set project | Reference dropped (can't happen for filtered loads — closure is closed; possible only for legacy fallback, matching today's behaviour) |
| `degree = 1` | Single worker; still re-stitches, so the re-stitch path is exercised by default in tests |

## Testing

xUnit in `tests/RoslynCodeLens.Tests/`, against the existing `FilterableSolution` fixture.

- **Re-stitch correctness** — `OpenAsync(filter)` returns the same project set and count as before; `ProjectReference` edges survive (e.g. `App.Api` still references `App.Domain`), so a cross-project `Compilation` resolves symbols across the boundary.
- **Parity** — parallel (`degree>1`) and sequential (`degree=1`) loads produce identical project-name sets and skipped lists.
- **Skipped-channel** — filtered-out projects still appear in `SkippedProject` with the `FilteredOut` reason; the perf change doesn't regress the filter contract.
- **Determinism** — `GetCompilationLevels` / downstream tools work on the re-stitched solution (smoke via `find_references` across the App.Api→App.Domain edge).

## Benchmark

`benchmarks/SolutionLoadBenchmarks` compares `degree=1` vs `degree=N` `OpenAsync` over `FilterableSolution`, and accepts `ROSLYN_CODELENS_BENCH_SOLUTION` to point at a large real solution.

Measured on the bundled fixture (ShortRun, 20-core box):

| Method | Mean | Allocated |
|---|---|---|
| sequential (`parallelism=1`) | ~5.7 s | 4.6 MB |
| parallel (default) | ~5.9 s | 11.9 MB |

**The fixture shows no parallel win, and that is expected — not a regression.** Its six projects form a single funnel (`App.Api.Tests → App.Api → App.{Domain,Infrastructure} → Shared.Common`): one root transitively pulls in everything, so there is nothing to open concurrently. It is the worst case for this strategy. The extra allocation is the re-stitch materialising document text in memory.

The win is real only when a solution has **multiple independent dependency roots** (the typical large solution: many services/apps over shared libraries) — those roots open on separate `BuildHost` processes in parallel. We can't represent that in a six-project fixture, so the 400-project payoff is reported qualitatively and the benchmark stands as the harness to confirm it on a real solution.

Crucially, the cost is **scoped**: the default unfiltered load of a healthy solution still takes the monolithic `OpenSolutionAsync` fast path and never touches this code. The pool runs only for (a) filtered loads — which a user opts into precisely because the solution is large — and (b) the legacy-project fallback. So the re-stitch memory/time overhead is confined to the paths where parallelism can pay for it, and the common small-solution case is unchanged.

## Out of scope

- **Async `load_solution` with a load handle** (deferred item b) — still the bigger surface change; revisited next.
- Shallow (no-transitive) project loading — no public `MSBuildWorkspace` API; the dedup strategy makes it unnecessary.
