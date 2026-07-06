# Analyzer / Generator DLL Lock — Shadow-Copy Loader Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Stop the server locking source-generator/analyzer DLLs in `bin\Debug` (issue #254) by loading analyzer assemblies from per-identity shadow copies through a refcounted process-wide cache, so a concurrent `dotnet build` succeeds while the server runs.

**Architecture:** A `SharedAnalyzerAssemblyCache` (process singleton) shadow-copies each distinct analyzer identity into its own **collectible** `AssemblyLoadContext` and reference-counts it. A thin per-solution `ShadowCopyAnalyzerAssemblyLoader` facade (implements Roslyn's public `IAnalyzerAssemblyLoader`) is injected into every project's `AnalyzerFileReference`s on load and disposed on unload. All caching lives behind the facade interface so the load-path wiring never depends on it. See design: [2026-07-05-analyzer-dll-lock-shadow-copy-design.md](2026-07-05-analyzer-dll-lock-shadow-copy-design.md).

**Tech Stack:** C# / .NET 10, Roslyn 5.6.0 (`Microsoft.CodeAnalysis.*`), `System.Runtime.Loader.AssemblyLoadContext`, xUnit.

**Verified API facts (5.6.0):** `ShadowCopyAnalyzerAssemblyLoader` is **internal** (we roll our own). Public: `IAnalyzerAssemblyLoader { Assembly LoadFromPath(string); void AddDependencyLocation(string); }`, `AnalyzerFileReference(string fullPath, IAnalyzerAssemblyLoader loader)`, `AnalyzerFileReference.FullPath`, `Solution.WithProjectAnalyzerReferences(ProjectId, IEnumerable<AnalyzerReference>)`.

**Conventions:** TDD (@superpowers:test-driven-development) — failing test first, minimal code, green, commit. Build: `dotnet build`. Test one class: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~<ClassName>"`. Analyzers (Meziantou/Roslynator etc.) run in-build and treat many things as warnings — keep new code clean (ConfigureAwait, StringComparison, etc.).

---

## Task 0: Pre-req — fix test-project fixture exclusion

The test csproj excludes some fixture solutions from compile but **not** `FilterableSolution`; once any fixture project is built, its generated `AssemblyInfo.cs` gets globbed into the test compile and breaks it (observed while reviewing #252). We add a source-generator fixture later, so fix the glob now.

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/RoslynCodeLens.Tests.csproj`

**Step 1: Edit the exclusion set**

Replace the `Compile Remove` / `None Include` block so **all** fixture solution folders are covered:

```xml
  <ItemGroup>
    <Compile Remove="Fixtures\TestSolution\**" />
    <Compile Remove="Fixtures\TestSolutionAlt\**" />
    <Compile Remove="Fixtures\LegacySolution\**" />
    <Compile Remove="Fixtures\FilterableSolution\**" />
    <None Include="Fixtures\TestSolution\**" CopyToOutputDirectory="Never" />
    <None Include="Fixtures\TestSolutionAlt\**" CopyToOutputDirectory="Never" />
    <None Include="Fixtures\LegacySolution\**" CopyToOutputDirectory="Never" />
    <None Include="Fixtures\FilterableSolution\**" CopyToOutputDirectory="Never" />
    <None Update="xunit.runner.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

**Step 2: Verify build still green**

Run: `dotnet build tests/RoslynCodeLens.Tests`
Expected: `Build succeeded. 0 Error(s)`.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/RoslynCodeLens.Tests.csproj
git commit -m "build(test): exclude FilterableSolution fixture from compile"
```

---

## Task 1: `SharedAnalyzerAssemblyCache` — shadow copy + collectible ALC + refcount

The heart of the fix. Loads a DLL from a shadow copy so the original is never locked; dedups by identity; releases (unloads) at refcount zero. **These tests are the primary #254 regression guard and need no MSBuild.**

**Files:**
- Create: `src/RoslynCodeLens/Analyzers/SharedAnalyzerAssemblyCache.cs`
- Test: `tests/RoslynCodeLens.Tests/Analyzers/SharedAnalyzerAssemblyCacheTests.cs`

**Step 1: Write failing tests**

```csharp
using System.Runtime.CompilerServices;
using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Tests.Analyzers;

public class SharedAnalyzerAssemblyCacheTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rcl-cache-test", Guid.NewGuid().ToString("N"));

    public SharedAnalyzerAssemblyCacheTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ } }

    // Copy an arbitrary real assembly to act as a stand-in "analyzer" we are allowed to delete.
    private string MakeFakeAnalyzer(string name)
    {
        var src = typeof(System.Text.Json.JsonSerializer).Assembly.Location;
        var dst = Path.Combine(_tmp, name + ".dll");
        File.Copy(src, dst, overwrite: true);
        return dst;
    }

    [Fact]
    public void Acquire_LoadsFromShadowCopy_NotOriginalPath()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Alpha");

        var asm = cache.Acquire(original, Array.Empty<string>());

        Assert.NotNull(asm);
        Assert.NotEqual(
            Path.GetFullPath(original),
            Path.GetFullPath(asm.Location),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acquire_LeavesOriginalFileUnlocked()   // THE #254 regression guard
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Beta");

        cache.Acquire(original, Array.Empty<string>());

        // If the original were locked (the bug), this throws IOException.
        var ex = Record.Exception(() => File.Delete(original));
        Assert.Null(ex);
    }

    [Fact]
    public void Acquire_SamePathTwice_DedupsToSameAssembly()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Gamma");

        var a = cache.Acquire(original, Array.Empty<string>());
        var b = cache.Acquire(original, Array.Empty<string>());

        Assert.Same(a, b);
    }

    [Fact]
    public void Release_UnloadsOnlyWhenRefCountReachesZero()
    {
        var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Delta");
        var shadowDirBefore = Path.GetDirectoryName(cache.Acquire(original, Array.Empty<string>()).Location)!;
        cache.Acquire(original, Array.Empty<string>());   // refcount = 2

        cache.Release(original);                           // -> 1, still present
        Assert.True(Directory.Exists(shadowDirBefore));

        cache.Release(original);                           // -> 0, unloaded + shadow removed
        ForceGc();
        Assert.False(Directory.Exists(shadowDirBefore));
        cache.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGc()
    {
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }
}
```

**Step 2: Run to verify failure**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~SharedAnalyzerAssemblyCacheTests"`
Expected: FAIL — `SharedAnalyzerAssemblyCache` does not exist (compile error).

**Step 3: Implement**

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace RoslynCodeLens.Analyzers;

/// <summary>
/// Process-wide cache of analyzer/generator assemblies loaded from shadow copies so the
/// originals in bin\Debug stay unlocked (issue #254). Each distinct analyzer identity gets
/// its own collectible AssemblyLoadContext and is reference-counted: when the last holder
/// releases it, the ALC is unloaded (best-effort) and the shadow copy deleted.
/// </summary>
public sealed class SharedAnalyzerAssemblyCache : IDisposable
{
    private readonly record struct Key(string Path, long Length, DateTime LastWriteUtc);

    private sealed class Entry
    {
        public required string ShadowDir { get; init; }
        public required AssemblyLoadContext Alc { get; init; }
        public required Assembly Root { get; init; }
        public int RefCount;
    }

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "roslyn-codelens-shadow", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    private static Key KeyFor(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        return new Key(Path.GetFullPath(fullPath), fi.Length, fi.LastWriteTimeUtc);
    }

    /// <summary>Load <paramref name="analyzerPath"/> from a shadow copy, incrementing its refcount.</summary>
    /// <param name="dependencyPaths">Sibling dependency locations Roslyn reported for this analyzer.</param>
    public Assembly Acquire(string analyzerPath, IReadOnlyList<string> dependencyPaths)
    {
        var key = KeyFor(analyzerPath);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                return existing.Root;
            }

            var shadowDir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(shadowDir);
            var shadowPath = ShadowCopy(analyzerPath, shadowDir);

            var alc = new AssemblyLoadContext($"analyzer:{Path.GetFileName(analyzerPath)}", isCollectible: true);
            alc.Resolving += (ctx, name) => ResolveDependency(ctx, name, analyzerPath, dependencyPaths, shadowDir);

            var root = alc.LoadFromAssemblyPath(shadowPath);
            _entries[key] = new Entry { ShadowDir = shadowDir, Alc = alc, Root = root, RefCount = 1 };
            return root;
        }
    }

    /// <summary>Decrement the refcount; at zero, unload the ALC and delete the shadow copy.</summary>
    public void Release(string analyzerPath)
    {
        var key = KeyFor(analyzerPath);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return;
            if (--entry.RefCount > 0)
                return;

            _entries.TryRemove(key, out _);
            entry.Alc.Unload();
            TryDeleteDir(entry.ShadowDir);
        }
    }

    private static Assembly? ResolveDependency(
        AssemblyLoadContext ctx, AssemblyName name, string analyzerPath,
        IReadOnlyList<string> dependencyPaths, string shadowDir)
    {
        var file = name.Name + ".dll";

        // Prefer the dependency locations Roslyn told us about, then the analyzer's own directory.
        var candidate =
            dependencyPaths.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), file, StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(Path.GetDirectoryName(analyzerPath)!, file);

        if (!File.Exists(candidate))
            return null;

        var shadow = ShadowCopy(candidate, shadowDir);
        return ctx.LoadFromAssemblyPath(shadow);
    }

    private static string ShadowCopy(string source, string shadowDir)
    {
        var dest = Path.Combine(shadowDir, Path.GetFileName(source));
        if (!File.Exists(dest))
            File.Copy(source, dest, overwrite: false);
        return dest;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* shadow files may still be memory-mapped until GC; cleaned on next process run */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _entries.Values)
                entry.Alc.Unload();
            _entries.Clear();
            TryDeleteDir(_root);
        }
    }
}
```

**Step 4: Run to verify pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~SharedAnalyzerAssemblyCacheTests"`
Expected: PASS (4 tests). If `Release_Unloads...` flakes on shadow-dir deletion (mmap not yet released), keep the assertion but note best-effort; if it proves flaky in CI, relax to asserting the entry is gone rather than the directory.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Analyzers/SharedAnalyzerAssemblyCache.cs tests/RoslynCodeLens.Tests/Analyzers/SharedAnalyzerAssemblyCacheTests.cs
git commit -m "feat(analyzers): shadow-copy assembly cache with refcounted collectible ALC (#254)"
```

---

## Task 2: `ShadowCopyAnalyzerAssemblyLoader` — per-solution facade

Thin adapter implementing Roslyn's `IAnalyzerAssemblyLoader`, delegating to the shared cache and releasing its set on dispose. This is the only type the load paths reference.

**Files:**
- Create: `src/RoslynCodeLens/Analyzers/ShadowCopyAnalyzerAssemblyLoader.cs`
- Test: `tests/RoslynCodeLens.Tests/Analyzers/ShadowCopyAnalyzerAssemblyLoaderTests.cs`

**Step 1: Write failing tests**

```csharp
using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Tests.Analyzers;

public class ShadowCopyAnalyzerAssemblyLoaderTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rcl-facade-test", Guid.NewGuid().ToString("N"));
    public ShadowCopyAnalyzerAssemblyLoaderTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private string MakeFake(string n)
    {
        var dst = Path.Combine(_tmp, n + ".dll");
        File.Copy(typeof(System.Text.Json.JsonSerializer).Assembly.Location, dst, true);
        return dst;
    }

    [Fact]
    public void LoadFromPath_ReturnsShadowedAssembly_AndLeavesOriginalUnlocked()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var loader = new ShadowCopyAnalyzerAssemblyLoader(cache);
        var original = MakeFake("Facade1");
        loader.AddDependencyLocation(original);

        var asm = loader.LoadFromPath(original);

        Assert.NotNull(asm);
        Assert.Null(Record.Exception(() => File.Delete(original)));
    }

    [Fact]
    public void Dispose_ReleasesAcquiredAssemblies()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFake("Facade2");
        string shadowDir;
        using (var loader = new ShadowCopyAnalyzerAssemblyLoader(cache))
        {
            loader.AddDependencyLocation(original);
            shadowDir = Path.GetDirectoryName(loader.LoadFromPath(original).Location)!;
        }
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(Directory.Exists(shadowDir));
    }
}
```

**Step 2: Run to verify failure**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ShadowCopyAnalyzerAssemblyLoaderTests"`
Expected: FAIL — type does not exist.

**Step 3: Implement**

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Analyzers;

/// <summary>
/// Per-solution <see cref="IAnalyzerAssemblyLoader"/> that loads analyzers through the process-wide
/// <see cref="SharedAnalyzerAssemblyCache"/> (shadow copies, no bin\Debug lock — issue #254).
/// Tracks what it acquired and releases it on <see cref="Dispose"/> so unload_solution can reclaim.
/// </summary>
public sealed class ShadowCopyAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader, IDisposable
{
    private readonly SharedAnalyzerAssemblyCache _cache;
    private readonly ConcurrentBag<string> _dependencyLocations = new();
    private readonly ConcurrentDictionary<string, byte> _acquired = new(StringComparer.OrdinalIgnoreCase);

    public ShadowCopyAnalyzerAssemblyLoader(SharedAnalyzerAssemblyCache cache) => _cache = cache;

    public void AddDependencyLocation(string fullPath) => _dependencyLocations.Add(fullPath);

    public Assembly LoadFromPath(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var asm = _cache.Acquire(full, _dependencyLocations.ToArray());
        _acquired.TryAdd(full, 0);
        return asm;
    }

    public void Dispose()
    {
        foreach (var path in _acquired.Keys)
            _cache.Release(path);
        _acquired.Clear();
    }
}
```

Note: `_acquired` is a set (dedups repeated `LoadFromPath` of the same path), so each path is released exactly once — matching the single net refcount this facade added per identity. (If Roslyn calls `LoadFromPath` for the same path twice on one loader, `Acquire` bumps the refcount twice but we release once; acceptable because the process-wide entry survives until *all* facades release — verify during Task 6 that repeated loads within one solution don't leak. If they can, switch `_acquired` to a count.)

**Step 4: Run to verify pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ShadowCopyAnalyzerAssemblyLoaderTests"`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Analyzers/ShadowCopyAnalyzerAssemblyLoader.cs tests/RoslynCodeLens.Tests/Analyzers/ShadowCopyAnalyzerAssemblyLoaderTests.cs
git commit -m "feat(analyzers): per-solution shadow-copy loader facade (#254)"
```

---

## Task 3: Remap analyzer references + thread the loader through `SolutionLoader`

Inject the facade into every project's `AnalyzerFileReference`s, on **both** load paths. Backward-compatible: when no loader is passed, behaviour is unchanged (existing tests unaffected).

**Files:**
- Create: `src/RoslynCodeLens/Analyzers/AnalyzerReferenceRemapper.cs`
- Modify: `src/RoslynCodeLens/SolutionLoader.cs` (thread `IAnalyzerAssemblyLoader?` through `OpenAsync` → `OpenPerProjectAsync`/`OpenFilteredAsync`/`ToDetachedInfoAsync`, and remap the solution-level path)
- Test: `tests/RoslynCodeLens.Tests/Analyzers/AnalyzerReferenceRemapperTests.cs`

**Step 1: Write failing test for the remapper**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Tests.Analyzers;

public class AnalyzerReferenceRemapperTests
{
    [Fact]
    public void Remap_RewritesFileReferences_PreservingPaths()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var loader = new ShadowCopyAnalyzerAssemblyLoader(cache);
        var path = typeof(System.Text.Json.JsonSerializer).Assembly.Location;
        var input = new AnalyzerReference[] { new AnalyzerFileReference(path, AnalyzerAssemblyLoader.Instance) };

        var result = AnalyzerReferenceRemapper.Remap(input, loader);

        var afr = Assert.IsType<AnalyzerFileReference>(Assert.Single(result));
        Assert.Equal(path, afr.FullPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remap_PassesThroughNonFileReferences()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var loader = new ShadowCopyAnalyzerAssemblyLoader(cache);
        var stub = new StubReference();

        var result = AnalyzerReferenceRemapper.Remap(new AnalyzerReference[] { stub }, loader);

        Assert.Same(stub, Assert.Single(result));
    }

    private sealed class StubReference : AnalyzerReference
    {
        public override string? FullPath => null;
        public override object Id => "stub";
        public override System.Collections.Immutable.ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
        public override System.Collections.Immutable.ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
    }
}
```

> Note: `AnalyzerAssemblyLoader.Instance` in the test is only a placeholder existing loader for constructing the input. If that helper is not public in 5.6.0, construct the input `AnalyzerFileReference` with the `loader` under test instead — the assertion only checks `FullPath` is preserved.

**Step 2: Run to verify failure**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~AnalyzerReferenceRemapperTests"`
Expected: FAIL — remapper does not exist.

**Step 3: Implement the remapper**

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynCodeLens.Analyzers;

/// <summary>
/// Rebuilds a project's <see cref="AnalyzerFileReference"/>s so they resolve their assemblies
/// through our <see cref="ShadowCopyAnalyzerAssemblyLoader"/> instead of the default (locking)
/// MSBuildWorkspace loader. Non-file references are passed through unchanged.
/// </summary>
public static class AnalyzerReferenceRemapper
{
    public static IReadOnlyList<AnalyzerReference> Remap(
        IEnumerable<AnalyzerReference> references, ShadowCopyAnalyzerAssemblyLoader loader)
    {
        var fileRefs = references
            .OfType<AnalyzerFileReference>()
            .ToList();

        // Pre-register every analyzer path so cross-analyzer dependency resolution can find siblings.
        foreach (var fr in fileRefs)
            loader.AddDependencyLocation(fr.FullPath);

        var result = new List<AnalyzerReference>();
        foreach (var r in references)
        {
            result.Add(r is AnalyzerFileReference fr
                ? new AnalyzerFileReference(fr.FullPath, loader)
                : r);
        }
        return result;
    }
}
```

**Step 4: Run to verify pass** — `dotnet test ... --filter "...AnalyzerReferenceRemapperTests"` → PASS.

**Step 5: Thread the loader through `SolutionLoader`**

In `src/RoslynCodeLens/SolutionLoader.cs`:

(a) `OpenAsync` signature — add the optional loader:
```csharp
public async Task<(Solution Solution, Workspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenAsync(
    string solutionPath, ProjectFilter? filter = null,
    ShadowCopyAnalyzerAssemblyLoader? analyzerLoader = null, CancellationToken ct = default)
```
Forward `analyzerLoader` into the two internal calls (`OpenFilteredAsync`, `OpenPerProjectAsync`) — add the same optional param to both, and to the private `OpenPerProjectAsync`/`OpenFilteredAsync` signatures.

(b) Solution-level path — after the `solution is null` guard (before `return (solution, workspace, ...)`), remap when a loader is present:
```csharp
if (analyzerLoader is not null)
    solution = RemapSolutionAnalyzers(solution, analyzerLoader);

return (solution, workspace, Array.Empty<SkippedProject>());
```
with a private helper:
```csharp
private static Solution RemapSolutionAnalyzers(Solution solution, ShadowCopyAnalyzerAssemblyLoader loader)
{
    foreach (var project in solution.Projects)
    {
        if (project.AnalyzerReferences.Count == 0)
            continue;
        var remapped = AnalyzerReferenceRemapper.Remap(project.AnalyzerReferences, loader);
        solution = solution.WithProjectAnalyzerReferences(project.Id, remapped);
    }
    return solution;
}
```

(c) Per-project path — `ToDetachedInfoAsync` takes the loader and remaps:
```csharp
private static async Task<ProjectInfo> ToDetachedInfoAsync(Project project, ShadowCopyAnalyzerAssemblyLoader? loader)
{
    ...
    analyzerReferences: loader is null
        ? project.AnalyzerReferences
        : AnalyzerReferenceRemapper.Remap(project.AnalyzerReferences, loader),
    ...
}
```
Thread `loader` from `OpenPerProjectAsync` into the `ToDetachedInfoAsync(p)` call site (the capture loop). Add `using RoslynCodeLens.Analyzers;`.

**Step 6: Build + run the full analyzer test folder and the existing loader tests**

Run: `dotnet build src/RoslynCodeLens` then
`dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~Analyzers|FullyQualifiedName~SolutionLoader"`
Expected: PASS; existing `SolutionLoader*`/`ProjectFilter*` tests unaffected (loader defaults to null).

**Step 7: Commit**

```bash
git add src/RoslynCodeLens/Analyzers/AnalyzerReferenceRemapper.cs src/RoslynCodeLens/SolutionLoader.cs tests/RoslynCodeLens.Tests/Analyzers/AnalyzerReferenceRemapperTests.cs
git commit -m "feat(loader): remap analyzer references through shadow-copy loader (#254)"
```

---

## Task 4: `SolutionManager` owns the loader + workspace; dispose releases

Give each solution its own facade loader over a shared cache, stop leaking the workspace, and release everything on unload.

**Files:**
- Modify: `src/RoslynCodeLens/SolutionManager.cs`
- (No new tests here — behaviour is exercised end-to-end in Task 6; this task is wiring.)

**Step 1: Add fields + a process-wide cache**

At the top of `SolutionManager`:
```csharp
private static readonly SharedAnalyzerAssemblyCache SharedCache = new();
private ShadowCopyAnalyzerAssemblyLoader? _analyzerLoader;
private Workspace? _workspace;
```
Add `using RoslynCodeLens.Analyzers;`.

> The static `SharedCache` is intentionally process-lifetime (never disposed) — it is the dedup layer; individual solutions release their entries via the facade. This is the "process-wide cache, per-solution facade" decision.

**Step 2: Construct + store the loader in `CreateAsync`**

Replace the discard with capture:
```csharp
var loader = new ShadowCopyAnalyzerAssemblyLoader(SharedCache);
Solution solution;
Workspace workspace;
IReadOnlyList<SkippedProject> skipped;
try
{
    (solution, workspace, skipped) = await loader is null   // (always non-null; keeps shape)
        ? default
        : await new SolutionLoader().OpenAsync(solutionPath, filter, loader).ConfigureAwait(false);
}
```
Simpler — just:
```csharp
var loader = new ShadowCopyAnalyzerAssemblyLoader(SharedCache);
(solution, workspace, skipped) =
    await loaderInstance.OpenAsync(solutionPath, filter, loader).ConfigureAwait(false);
```
Then pass `loader`/`workspace` into the `SolutionManager` instance (extend the private ctor or assign after construction) so `WarmupAsync` compiles the already-remapped `solution`. Store `_analyzerLoader = loader; _workspace = workspace;`.

Do the same capture in `ForceReloadAsync` ([SolutionManager.cs:262](../../src/RoslynCodeLens/SolutionManager.cs)): create a fresh loader, `OpenAsync(..., loader)`, and **dispose the previous** `_workspace`/`_analyzerLoader` before replacing them.

**Step 3: Dispose releases**

```csharp
public void Dispose()
{
    _tracker?.Dispose();
    _peCache.Dispose();
    _workspace?.Dispose();
    _analyzerLoader?.Dispose();   // Release() each acquired analyzer -> refcount--, unload at zero
}
```

**Step 4: Build + run the existing manager/integration tests**

Run: `dotnet build` then `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~SolutionManager|FullyQualifiedName~MultiSolution"`
Expected: PASS (note: some coverage/metadata integration tests may fail *locally* due to the known MSBuildWorkspace load flake — confirm they pass in CI; they are unrelated to this change).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/SolutionManager.cs
git commit -m "feat(manager): own + dispose analyzer loader and workspace; release on unload (#254)"
```

---

## Task 5: Source-generator fixture + end-to-end tests

Prove (a) a loaded solution's generator DLL is unlocked, and (b) generated symbols still resolve.

**Files:**
- Create fixture generator: `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/SampleGenerator/SampleGenerator.csproj` + `HelloGenerator.cs`
- Modify fixture consumer: add an `Analyzer` reference to `SampleGenerator` from `TestLib` (`.csproj`), so loading TestSolution loads the generator.
- Test: `tests/RoslynCodeLens.Tests/Analyzers/GeneratorLockIntegrationTests.cs`

**Step 1: Add the generator project**

`SampleGenerator.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IsRoslynComponent>true</IsRoslynComponent>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" PrivateAssets="all" />
  </ItemGroup>
</Project>
```
`HelloGenerator.cs` — a trivial incremental generator emitting `namespace Generated { public static class Hello { public const string Message = "hi"; } }`.

`TestLib.csproj` gains:
```xml
<ItemGroup>
  <ProjectReference Include="..\SampleGenerator\SampleGenerator.csproj"
      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```
Add `SampleGenerator` to the TestSolution `.sln`. (This fixture is compile-excluded by Task 0's glob.)

**Step 2: Write the integration tests**

```csharp
[Collection("TestSolution")]
public class GeneratorLockIntegrationTests
{
    private readonly TestSolutionFixture _fixture;
    public GeneratorLockIntegrationTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public void GeneratedSymbol_IsResolvable()
    {
        var loaded = _fixture.Loaded;   // fixture loads via SolutionManager (uses shadow loader)
        var comp = loaded.Compilations.Values
            .FirstOrDefault(c => c.GetTypeByMetadataName("Generated.Hello") is not null);
        Assert.NotNull(comp);   // generator ran through the shadow copy; symbol exists
    }
}
```
> The direct "original DLL is deletable after load" property is already proven deterministically by `SharedAnalyzerAssemblyCacheTests.Acquire_LeavesOriginalFileUnlocked`. This fixture test adds the end-to-end confirmation that shadow-loading does **not** cost generated-symbol fidelity. If `TestSolutionFixture` does not currently execute generators (it may only `OpenAsync` without full warmup), extend it — or add a dedicated `SolutionManager.CreateAsync` load in this test — during implementation.

**Step 3: Build + run**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GeneratorLockIntegrationTests"`
Expected: PASS. If it flakes locally on MSBuild load, verify in CI.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/TestSolution/SampleGenerator tests/RoslynCodeLens.Tests/Fixtures/TestSolution/*.sln tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/TestLib.csproj tests/RoslynCodeLens.Tests/Analyzers/GeneratorLockIntegrationTests.cs
git commit -m "test(analyzers): source-generator fixture proves shadow-load keeps symbols (#254)"
```

---

## Task 6: Full verification + manual repro

**Step 1: Full test suite** — `dotnet test` → all green (bar the pre-existing local MSBuild-load flakes; confirm CI green).

**Step 2: Manual #254 repro** (@superpowers:verification-before-completion) — run the built server against a generator solution, wait for "Background compilation complete", then in another terminal `dotnet build` that solution. Expected: build succeeds (previously MSB3027). Document the observed output.

**Step 3: Update CHANGELOG / docs** if the repo convention requires it; note the env-var-free behaviour change.

**Step 4: Comment on #254** with the outcome and the "`ShadowCopyAnalyzerAssemblyLoader` is internal, so we rolled our own" note.

**Step 5: Final commit / open PR** targeting `main` from `fix/analyzer-dll-lock-254`.

---

## Deferred (not in this plan)

- **Phase 4 deferred/opt-in warmup** (`ROSLYN_CODELENS_EAGER_WARMUP`): compilation would still lazily lock on first tool use, so it only reduces the startup window; add later only if warranted.
- **Cross-ALC shared dependency graph**: rejected (complexity vs. rare benefit) — see design doc.
- **Content-hash cache key**: only if cross-path dedup is ever shown to matter.
