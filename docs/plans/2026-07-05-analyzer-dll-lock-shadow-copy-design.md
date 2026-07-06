# Analyzer / Source-Generator DLL Lock — Shadow-Copy Loader

**Date:** 2026-07-05
**Status:** Design approved (loader scope resolved) — ready for implementation plan
**Issue:** [#254](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/254) — Source-generator/analyzer DLL is locked during background compilation, breaking `dotnet build` (MSB3027)
**Upstream:** [dotnet/roslyn#78196](https://github.com/dotnet/roslyn/issues/78196)

## Motivation

When the server loads a solution containing a source generator (or analyzer), the generator
assembly is locked on disk for the lifetime of the `roslyn-codelens` process. Any subsequent
`dotnet build` of that solution fails with `MSB3027`/`MSB3021` because MSBuild cannot overwrite
the locked DLL in `bin\Debug`. Developers can't build or test from the CLI while the server
runs; the only recovery is killing the process.

## Verified root cause

All three mechanisms in the issue were confirmed against `main` (Roslyn **5.6.0**):

1. **Eager background compilation takes the lock at startup.**
   `SolutionManager.CreateAsync` → `WarmupAsync` ([SolutionManager.cs:81](../../src/RoslynCodeLens/SolutionManager.cs)) →
   `CompileAllParallelAsync` runs `project.GetCompilationAsync()` for **every** project
   ([SolutionLoader.cs:463](../../src/RoslynCodeLens/SolutionLoader.cs)). Executing the
   compilation runs source generators, which loads + locks their assemblies — before any tool
   is invoked.

2. **`MSBuildWorkspace` uses the default (non-shadow-copying) analyzer loader.**
   `MSBuildWorkspace.Create()` ([SolutionLoader.cs:82](../../src/RoslynCodeLens/SolutionLoader.cs),
   [:202](../../src/RoslynCodeLens/SolutionLoader.cs)) is used with no custom
   `IAnalyzerAssemblyLoader`, so generator DLLs load straight from `bin\Debug` and stay locked.
   The per-project loader even copies `project.AnalyzerReferences` verbatim into the re-stitched
   `AdhocWorkspace` ([SolutionLoader.cs:362](../../src/RoslynCodeLens/SolutionLoader.cs)),
   carrying the locking references forward.

3. **`unload_solution` cannot release it.**
   `SolutionManager.Dispose()` ([SolutionManager.cs:293](../../src/RoslynCodeLens/SolutionManager.cs))
   only disposes `_tracker` and `_peCache`. It does not even hold the `MSBuildWorkspace` — both
   `CreateAsync` and `ForceReloadAsync` discard it (`(solution, _, skipped)`). And the default
   loader loads analyzers into a **non-collectible** load context, so the assemblies survive
   until process exit regardless.

   **Bonus finding:** the `MSBuildWorkspace` from the solution-level path is leaked entirely
   (never disposed), not just the analyzer lock.

## Key constraint discovered (changes the suggested fix)

The issue suggests `ShadowCopyAnalyzerAssemblyLoader`. **That type is `internal` in Roslyn
5.6.0** — verified by reflecting over the full referenced surface (Common, CSharp, Workspaces,
Features, CSharp.Features, Workspaces.MSBuild, Scripting): there is **no public shadow-copy
loader and no public `IAnalyzerAssemblyLoader` implementation** at all.

What *is* public:

- `Microsoft.CodeAnalysis.IAnalyzerAssemblyLoader` — interface, exactly two members:
  - `Assembly LoadFromPath(string fullPath)`
  - `void AddDependencyLocation(string fullPath)`
- `Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference(string fullPath, IAnalyzerAssemblyLoader loader)` — ctor.

**Conclusion:** we implement our own shadow-copying `IAnalyzerAssemblyLoader` (there is no
built-in public one to reuse), and inject it by re-constructing each project's
`AnalyzerFileReference`s with our loader.

## Goals

- CLI `dotnet build`/`dotnet test` of a loaded solution succeeds while the server is running.
- Generated symbols and analyzer diagnostics remain available (no loss of semantic fidelity —
  we shadow-copy generators, we do **not** disable them).
- Analyzer assemblies are **deduplicated across solutions** (many/long-lived usage) rather than
  copied+loaded once per solution.
- `unload_solution` makes a best-effort release of analyzer assemblies (collectible load context).

## Non-goals

- Reusing Roslyn's internal loader via reflection (fragile across versions — rejected).
- Guaranteeing synchronous unload of native/pinned generator assemblies (collectible ALC is
  best-effort; some generators pin themselves — documented, not fixed here).
- Changing the eager-warmup model as the *primary* fix (see Phase 4 — optional deferral only).

## Loader scope decision (resolved)

The server is used in a **many / long-lived** mode — a persistent process accumulating many
solutions over a session. That rules out both pure options:

- **Pure per-solution loader** duplicates the same analyzer (e.g. one popular generator version
  shared by N loaded solutions) N× on disk and in memory — real bloat over a long session.
- **Pure process-wide loader** dedups but never releases; a long-lived server's analyzer memory
  only grows and `unload_solution` reclaims nothing.

**Chosen: a refcounted shared cache behind a per-solution facade.** Analyzer assemblies are
shared across solutions (dedup) but each distinct identity lives in its own collectible load
context with a reference count, so the last solution to unload it triggers release (reclaim).

Two sub-decisions, with rationale:

- **Key = `(fullPath, fileLength, lastWriteTimeUtc)`.** The dominant dedup case is many solutions
  referencing the *same* NuGet-restored analyzer path, so path alone carries most of the win;
  `size + mtime` guards the rebuilt-`bin\Debug`-generator case. Content hashing buys cross-path
  dedup that effectively never occurs for analyzers — **not** done (YAGNI).
- **Self-contained per-identity ALC** (analyzer + its sibling deps shadow-copied into one entry
  and resolved within that entry's ALC). We deliberately **do not** build a shared cross-ALC
  dependency graph: cross-ALC type identity + resolution ordering + partial unload all having to
  be correct at once is the classic source of load-context bugs, and it only dedups a *dependency*
  lib shared between *different* analyzers — rare. Self-contained ALCs keep the dominant dedup
  (same generator across many solutions = one identity = one ALC) at a fraction of the risk.

**Isolation principle:** the caching strategy lives entirely behind the per-solution
`IAnalyzerAssemblyLoader` facade. The load-path integration (Phase 2) and dispose wiring (Phase 3)
depend only on the interface, never on the cache. Consequences: the build-lock fix (the actual
bug) is testable with a trivial backing store; the shared cache + refcounting is a swappable
component with its own tests; if unload-release proves to reclaim little in practice, the cache
can be dialled back to dead-simple without touching the load paths.

> **Reality check on release.** A collectible ALC only frees after GC drops every compilation and
> symbol rooted in it, and a generator that spins up threads / registers statics / loads native
> libs pins itself and never unloads. Refcount-zero is therefore a *request*; release is partial
> and non-deterministic. We do **not** predicate the fix on reliable reclaim — the build-lock fix
> (shadow copy) stands on its own.

## Design

### Phase 1 — Shadow-copy loader + shared cache

Two collaborating types (new files under `src/RoslynCodeLens/`):

**`SharedAnalyzerAssemblyCache`** — process-wide singleton, the swappable backing store:

```csharp
internal sealed class SharedAnalyzerAssemblyCache
{
    private readonly record struct AnalyzerKey(string FullPath, long Length, DateTime LastWriteUtc);
    private sealed class Entry
    {
        public string ShadowDir = "";
        public AssemblyLoadContext Alc = default!;   // isCollectible: true, one per identity
        public int RefCount;
        public Assembly? Root;
    }
    private readonly ConcurrentDictionary<AnalyzerKey, Entry> _entries = new();

    public Assembly Acquire(string fullPath, IReadOnlyList<string> dependencyPaths);  // refcount++, create-on-first
    public void Release(string fullPath);                                             // refcount--, Unload+delete at 0
}
```

- **Self-contained ALC:** on first `Acquire`, shadow-copy the analyzer *and* its
  `dependencyPaths` (the sibling DLLs supplied via `AddDependencyLocation`) into the entry's
  shadow dir; the ALC's `Resolving` handler serves deps from that dir by simple name.
- **Thread-safety:** `Acquire`/`Release` run under a per-entry lock (parallel warmup calls them
  concurrently). Idempotent copy: reuse a byte-identical existing shadow file.
- **Release:** at refcount 0, `Alc.Unload()` (best-effort — see reality check) and recursively
  delete the shadow dir; drop the entry.

**`ShadowCopyAnalyzerAssemblyLoader`** — thin per-solution facade implementing the public
`IAnalyzerAssemblyLoader`, owned by one `SolutionManager`:

```csharp
internal sealed class ShadowCopyAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader, IDisposable
{
    private readonly SharedAnalyzerAssemblyCache _cache;
    private readonly ConcurrentBag<string> _dependencyLocations = new();  // seen via AddDependencyLocation
    private readonly HashSet<string> _acquired = new(OrdinalIgnoreCase);  // for symmetric release

    public void AddDependencyLocation(string fullPath) => _dependencyLocations.Add(fullPath);
    public Assembly LoadFromPath(string fullPath)      // Acquire(fullPath, siblingDeps); track in _acquired
    public void Dispose()                              // Release each path in _acquired
}
```

The facade is the *only* thing the load paths know about; swapping `SharedAnalyzerAssemblyCache`
for a trivial store changes nothing else.

### Phase 2 — Inject the loader into both load paths

Add a helper (in `SolutionLoader` or a small `AnalyzerReferenceRemapper`):

```csharp
// Rebuild every AnalyzerFileReference in a project to use `loader`.
static IReadOnlyList<AnalyzerReference> Remap(IEnumerable<AnalyzerReference> refs, IAnalyzerAssemblyLoader loader)
```

For each `AnalyzerFileReference`, pre-call `loader.AddDependencyLocation(afr.FullPath)` for the
whole project's analyzer set, then emit `new AnalyzerFileReference(afr.FullPath, loader)`.
Non-file analyzer references (rare) pass through unchanged.

Two application points — **one facade loader per loaded solution** (owned by `SolutionManager`,
so its lifetime matches unload; it shares assemblies through the process-wide cache):

- **Per-project / re-stitch path:** in `ToDetachedInfoAsync`
  ([SolutionLoader.cs:349-365](../../src/RoslynCodeLens/SolutionLoader.cs)) pass
  `analyzerReferences: Remap(project.AnalyzerReferences, loader)`.
- **Solution-level path:** after `OpenSolutionAsync` succeeds
  ([SolutionLoader.cs:93-96](../../src/RoslynCodeLens/SolutionLoader.cs)) and **before** warmup
  compiles, rewrite each project:
  `solution = solution.WithProjectAnalyzerReferences(projectId, Remap(...))` (or remove+add).
  Because generators only execute at `GetCompilationAsync` time (during warmup, after open),
  remapping between open and warmup is sufficient — the original is never loaded in-process.

Threading the loader through requires `OpenAsync`/`OpenPerProjectAsync` to accept (or return) the
loader. Cleanest: `SolutionManager.CreateAsync` constructs the loader, passes it into
`loader.OpenAsync(...)`, and stores it for disposal.

### Phase 3 — Own and dispose the workspace + loader

- `SolutionManager` gains `private ShadowCopyAnalyzerAssemblyLoader? _analyzerLoader;` (the
  per-solution facade) and `private Workspace? _workspace;` (stop discarding it).
- `Dispose()` ([SolutionManager.cs:293](../../src/RoslynCodeLens/SolutionManager.cs)) also
  disposes `_workspace` and `_analyzerLoader` — the facade `Release`s its acquired set, dropping
  each shared-cache refcount and unloading + deleting any entry that hits zero. This is what
  finally lets `unload_solution`
  ([MultiSolutionManager.cs:226](../../src/RoslynCodeLens/MultiSolutionManager.cs)) release the
  lock (best-effort; see reality check above).
- `ForceReloadAsync` ([SolutionManager.cs:262](../../src/RoslynCodeLens/SolutionManager.cs)) must
  dispose the previous workspace/loader before replacing them.

### Phase 4 — (Optional) deferred warmup

Secondary, not required for the fix: gate eager warmup behind an env var
(`ROSLYN_CODELENS_EAGER_WARMUP`, default on) so users on generator-heavy solutions can defer the
lock/CPU until first tool use. Compilation still happens lazily on first tool call, so this only
moves the (now shadow-copied, harmless) lock later. Include only if cheap.

## Testing

New fixture: a minimal `netstandard2.0` **incremental source generator** referenced by a target
project via `OutputItemType="Analyzer"` / `ReferenceOutputAssembly="false"` — under
`tests/RoslynCodeLens.Tests/Fixtures/` (add it to the `<Compile Remove>` exclusion set in the
test csproj, same as the other fixtures — see note below).

1. **Unit — loader shadow-copies:** `LoadFromPath` returns an assembly whose `Location` is under
   the shadow root, not the original path; second call returns the same instance.
2. **Integration — lock released:** load the generator fixture solution, run warmup, then assert
   the original generator DLL in `bin\Debug` **can be overwritten/deleted** (proves it is
   unlocked). This is the direct regression test for #254.
3. **Integration — semantics preserved:** a symbol emitted by the generator still resolves
   through the normal tools (generated code is still compiled, just from a shadow copy).
4. **Unit — dispose unloads:** after `SolutionManager.Dispose()`, the shadow directory is
   deleted (best-effort) and no handle to the original remains.

**Pre-req fix (separate, tiny):** the test csproj excludes `TestSolution`/`TestSolutionAlt`/
`LegacySolution` from `<Compile Remove>` but **not** `FilterableSolution`; any new fixture
solution must be added to that list or its generated `AssemblyInfo.cs` breaks the test compile
(observed during investigation of #252). Add the new fixture — and `FilterableSolution` — to the
exclusion set.

## Risks & mitigations

- **Custom loader correctness (dependency resolution).** Roslyn's internal loader handles edge
  cases (native DLLs, culture resources, version redirects). Mitigate: resolve siblings by simple
  name from the analyzer's own directory; log + fall through on miss. Generators with exotic
  loading may still fail — acceptable, and no worse than a hard lock.
- **Collectible ALC won't fully unload if a generator pins itself** (static state, unmanaged
  handles). `unload_solution` release is therefore **best-effort**; document it. The build-lock
  fix (shadow copy) does not depend on unload working.
- **Shadow-copy disk/latency cost** at load: bounded by analyzer-set size; copies are per-solution
  and cleaned on unload. Negligible vs. compilation cost.
- **`WithProjectAnalyzerReferences` API shape** — verify the exact `Solution`/`Project` mutation
  method at implementation time (remove+add is the fallback).

## Rollout

1. Land loader + injection + dispose (Phases 1-3) with the generator fixture and the four tests.
2. `dotnet build` + `dotnet test` green; manually reproduce #254 against the fixture and confirm
   a concurrent CLI build now succeeds.
3. Optional Phase 4 in a follow-up if desired.
4. Comment on #254 with the outcome and the `ShadowCopyAnalyzerAssemblyLoader`-is-internal note
   (so future readers know why we rolled our own).

## Resolved decisions

- **Loader scope:** refcounted process-wide shared cache behind a per-solution facade (dedup +
  best-effort release). See "Loader scope decision" above. Driven by many/long-lived usage.
- **Cache key:** `(fullPath, fileLength, lastWriteTimeUtc)` — no content hashing.
- **ALC granularity:** self-contained per analyzer identity — no shared cross-ALC dependency graph.
- **Isolation:** caching strategy sits entirely behind the `IAnalyzerAssemblyLoader` facade, so the
  build-lock fix and the cache are independently testable and the cache is swappable.

## Remaining questions for the implementation plan

- Exact `Solution`/`Project` mutation API for rewriting analyzer references
  (`WithProjectAnalyzerReferences` vs remove+add) — verify against 5.6.0 at implementation time.
- How the facade discovers an analyzer's sibling dependency paths for `Acquire` — from the
  `AddDependencyLocation` calls Roslyn makes, vs. scanning the analyzer's directory. Confirm the
  ordering (are all `AddDependencyLocation` calls made before the first `LoadFromPath`?).
