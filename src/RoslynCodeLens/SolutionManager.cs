using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynCodeLens.Analyzers;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens;

public sealed class SolutionManager : IDisposable
{
    private LoadedSolution _loaded;
    private SymbolResolver _resolver;
    private MetadataSymbolResolver _metadataResolver;
    private readonly string? _solutionPath;
    private FileChangeTracker? _tracker;
    private readonly Lock _lock = new();

    // Serializes every operation that produces a new loaded solution — the watcher-driven
    // auto-rebuild (RebuildIfStale) and the explicit rebuild_solution (ForceReloadAsync). Only one
    // runs at a time so neither can read _loaded, have the other swap + dispose its workspace, and
    // then clobber the swap with a solution forked off a now-disposed workspace. Auto-rebuild
    // acquires it opportunistically (skips if busy, returning current data); ForceReloadAsync waits.
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private Task? _warmupTask;
    private Exception? _warmupException;
    private readonly Exception? _loadException;
    private readonly string? _loadFailureMessage;
    private readonly PEFileCache _peCache = new();
    private readonly IlDisassemblerAdapter _ilAdapter;

    // Process-lifetime shared shadow-copy cache (never disposed): the cross-solution
    // dedup layer. Individual solutions release their entries via their own facade
    // analyzer loader on dispose (refcount--), so entries unload when the last owner goes.
    private static readonly SharedAnalyzerAssemblyCache SharedCache = new();
    private ShadowCopyAnalyzerAssemblyLoader? _analyzerLoader;
    private Workspace? _workspace;
    private bool _disposed;

    private SolutionManager(LoadedSolution loaded, string? solutionPath)
    {
        _loaded = loaded;
        _solutionPath = solutionPath;
        _resolver = new SymbolResolver(loaded);
        _metadataResolver = new MetadataSymbolResolver(loaded, _resolver);
        _ilAdapter = new IlDisassemblerAdapter(_peCache);
    }

    private SolutionManager(string solutionPath, Exception loadException)
    {
        _loaded = LoadedSolution.Empty;
        _solutionPath = solutionPath;
        _resolver = new SymbolResolver(_loaded);
        _metadataResolver = new MetadataSymbolResolver(_loaded, _resolver);
        _ilAdapter = new IlDisassemblerAdapter(_peCache);
        _loadException = loadException;
        _loadFailureMessage = SolutionLoadFailure.Describe(solutionPath, loadException);
    }

    public bool HasLoadFailure => _loadException != null;
    public string? LoadFailureMessage => _loadFailureMessage;

    public static async Task<SolutionManager> CreateAsync(string solutionPath, ProjectFilter? filter = null)
    {
        var solutionLoader = new SolutionLoader();
        var analyzerLoader = new ShadowCopyAnalyzerAssemblyLoader(SharedCache);
        SolutionLoader.SolutionOpen open;
        try
        {
            open = await solutionLoader.OpenAsync(solutionPath, filter, analyzerLoader).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            analyzerLoader.Dispose();
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] {SolutionLoadFailure.Describe(solutionPath, ex)}").ConfigureAwait(false);
            return new SolutionManager(solutionPath, ex);
        }

        var emptyLoaded = new LoadedSolution
        {
            Solution = open.Solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(),
            SkippedProjects = open.Skipped,
            LoadDiagnostics = open.LoadDiagnostics
        };

        var manager = new SolutionManager(emptyLoaded, solutionPath);
        manager._analyzerLoader = analyzerLoader;
        manager._workspace = open.Workspace;
        manager._warmupTask = manager.WarmupAsync(solutionLoader, open.Solution, open.Skipped, open.LoadDiagnostics);
        return manager;
    }

    public static SolutionManager CreateEmpty()
    {
        return new SolutionManager(LoadedSolution.Empty, null);
    }

    private async Task WarmupAsync(SolutionLoader loader, Solution solution, IReadOnlyList<SkippedProject> skipped, IReadOnlyList<string> loadDiagnostics)
    {
        try
        {
            await Console.Error.WriteLineAsync("[roslyn-codelens] Background compilation starting...").ConfigureAwait(false);
            var compilations = await loader.CompileAllParallelAsync(solution).ConfigureAwait(false);

            var newLoaded = new LoadedSolution
            {
                Solution = solution,
                Compilations = compilations,
                SkippedProjects = skipped,
                LoadDiagnostics = loadDiagnostics
            };
            var newResolver = new SymbolResolver(newLoaded);
            var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

            lock (_lock)
            {
                _loaded = newLoaded;
                _resolver = newResolver;
                _metadataResolver = newMetadataResolver;

                if (_solutionPath != null)
                {
                    _tracker = new FileChangeTracker(newLoaded, _solutionPath);
                    _tracker.OnDllChanged = path => _peCache.Invalidate(path);
                }
            }

            await Console.Error.WriteLineAsync($"[roslyn-codelens] Background compilation complete. {compilations.Count} project(s) compiled.").ConfigureAwait(false);
            _warmupTask = null;
        }
        catch (Exception ex)
        {
            _warmupException = ex;
            await Console.Error.WriteLineAsync($"[roslyn-codelens] Background compilation failed: {ex}").ConfigureAwait(false);
        }
    }

    public Task WaitForWarmupAsync()
    {
        return _warmupTask ?? Task.CompletedTask;
    }

    public LoadedSolution GetLoadedSolution()
    {
        ThrowIfLoadFailed();
        _warmupTask?.GetAwaiter().GetResult();
        if (_warmupException != null)
            throw new InvalidOperationException("Solution warmup failed.", _warmupException);
        RebuildIfStale();
        return _loaded;
    }

    public SymbolResolver GetResolver()
    {
        ThrowIfLoadFailed();
        _warmupTask?.GetAwaiter().GetResult();
        if (_warmupException != null)
            throw new InvalidOperationException("Solution warmup failed.", _warmupException);
        RebuildIfStale();
        return _resolver;
    }

    public MetadataSymbolResolver GetMetadataResolver()
    {
        ThrowIfLoadFailed();
        _warmupTask?.GetAwaiter().GetResult();
        if (_warmupException != null)
            throw new InvalidOperationException("Solution warmup failed.", _warmupException);
        RebuildIfStale();
        return _metadataResolver;
    }

    public IlDisassemblerAdapter GetIlDisassembler() => _ilAdapter;

    public void EnsureLoaded()
    {
        ThrowIfLoadFailed();
        _warmupTask?.GetAwaiter().GetResult();
        if (_warmupException != null)
            throw new InvalidOperationException("Solution warmup failed.", _warmupException);
        if (_loaded.IsEmpty)
            throw new InvalidOperationException(
                DescribeEmptyWorkspace(_solutionPath, _loaded.SkippedProjects));
    }

    /// <summary>
    /// An empty workspace has two very different causes and they need different messages. Saying
    /// "No .sln file found" when a solution WAS found but every project failed to open sends the
    /// reader off hunting for a missing file instead of at the load failure that actually happened
    /// (issue #399 was reported partly on the strength of this message).
    /// </summary>
    internal static string DescribeEmptyWorkspace(string? solutionPath, IReadOnlyList<SkippedProject> skipped)
    {
        if (solutionPath is null)
        {
            return "No solution found. Either run from a directory containing a .sln/.slnx file, " +
                   "or pass the solution path as argument: roslyn-codelens-mcp /path/to/Solution.sln";
        }

        var name = Path.GetFileName(solutionPath);
        if (skipped.Count == 0)
            return $"Solution '{name}' loaded but contains no projects that could be compiled.";

        var reasons = string.Join("; ", skipped.Take(5).Select(p => $"{p.Name}: {p.Reason}"));
        var more = skipped.Count > 5 ? $" (and {skipped.Count - 5} more)" : "";
        return $"Solution '{name}' loaded but all {skipped.Count} project(s) were skipped, so there is " +
               $"nothing to query. Reasons: {reasons}{more}";
    }

    private void ThrowIfLoadFailed()
    {
        if (_loadException != null)
            throw new InvalidOperationException(_loadFailureMessage!, _loadException);
    }

    private void RebuildIfStale()
    {
        if (_tracker == null || !_tracker.HasStaleProjects)
            return;

        // Opportunistic acquire: if a rebuild or a full reload (ForceReloadAsync) is already
        // running, skip and let this query return the current consistent data rather than start a
        // competing rebuild.
        if (!_rebuildGate.Wait(0))
            return;

        try
        {
            // Re-check under the gate: another rebuild may have drained the stale state between the
            // pre-check above and our acquiring the gate.
            if (_tracker == null || !_tracker.HasStaleProjects)
                return;

            // Drain (snapshot + clear) so edits that land DURING this rebuild accumulate into the
            // freshly-emptied tracker and trigger the next rebuild, instead of being wiped by a
            // blanket clear afterwards (that silently dropped saves made during a multi-second
            // rebuild). On failure we restore the drained state so it retries.
            var snapshot = _tracker.DrainStale();
            Console.Error.WriteLine(
                $"[roslyn-codelens] Rebuilding {snapshot.StaleProjectIds.Count} stale project(s)...");

            try
            {
                RebuildAsync(snapshot).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _tracker.RestoreStale(snapshot);
                Console.Error.WriteLine(
                    $"[roslyn-codelens] Rebuild failed: {ex}. Using cached data.");
            }
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private async Task RebuildAsync(FileChangeTracker.StaleSnapshot snapshot)
    {
        // A pure .cs edit is applied to the existing Solution in place, preserving every
        // ProjectId/DocumentId and keeping the recompiled symbols identity-consistent with
        // loaded.Solution. Re-opening the solution (the #282 bug) minted fresh ProjectIds
        // that no longer matched the tracker's stale ids or the retained compilations, so
        // no project actually recompiled and SymbolFinder silently returned empty. Structural
        // changes (project files, new files) still need MSBuild re-evaluation via a full reload.
        if (!snapshot.RequiresFullReload
            && await TryRebuildIncrementalAsync(snapshot.StaleProjectIds, snapshot.ChangedDocumentPaths).ConfigureAwait(false))
        {
            return;
        }

        await RebuildViaFullReloadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the changed source files' current on-disk text to the existing solution via
    /// <see cref="Solution.WithDocumentText(DocumentId, Microsoft.CodeAnalysis.Text.SourceText, PreservationMode)"/>,
    /// then recompiles only the stale projects. Because the solution snapshot lineage is
    /// preserved, the stale ids still resolve, unchanged projects reuse their carried-over
    /// compilations, and the resulting symbols stay valid against the same solution — so
    /// <c>SymbolFinder</c> works. Returns false (escalate to a full reload) if a changed file
    /// can't be read or isn't a document in the current solution.
    /// </summary>
    private async Task<bool> TryRebuildIncrementalAsync(
        IReadOnlySet<ProjectId> staleIds, IReadOnlyList<string> changedDocumentPaths)
    {
        var solution = _loaded.Solution;

        foreach (var path in changedDocumentPaths)
        {
            var docIds = solution.GetDocumentIdsWithFilePath(path);
            if (docIds.IsDefaultOrEmpty)
                return false; // no longer a known document — reload so the graph re-evaluates

            var text = await TryReadSourceTextAsync(path).ConfigureAwait(false);
            if (text == null)
                return false; // deleted or unreadable — reload

            foreach (var docId in docIds)
                solution = solution.WithDocumentText(docId, text);
        }

        await RecompileAndSwapAsync(solution, staleIds).ConfigureAwait(false);

        await Console.Error.WriteLineAsync("[roslyn-codelens] Rebuild complete (incremental).").ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Commits already-written document texts to the in-memory snapshot immediately, so queries
    /// issued right after a tool wrote files (rename_symbol apply) see the new text instead of
    /// waiting out the file watcher's debounce window. Serialized with the watcher auto-rebuild
    /// and ForceReloadAsync via <see cref="_rebuildGate"/> (waits, like ForceReloadAsync does),
    /// then reuses the incremental-rebuild core: <c>WithDocumentText</c> per document, recompile
    /// the changed projects plus their transitive dependents (mirroring the watcher's
    /// transitive staleness), swap the snapshot, and re-map the tracker. Document ids that no
    /// longer exist (a full reload swapped in fresh ids mid-flight) are skipped — that reload
    /// already read the committed text from disk.
    /// </summary>
    public async Task CommitDocumentTextsAsync(
        IReadOnlyList<(DocumentId Id, Microsoft.CodeAnalysis.Text.SourceText Text)> documents,
        CancellationToken ct = default)
    {
        if (documents.Count == 0)
            return;

        await _rebuildGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var solution = _loaded.Solution;
            var changedProjects = new HashSet<ProjectId>();
            foreach (var (docId, text) in documents)
            {
                if (solution.GetDocument(docId) == null)
                    continue;
                solution = solution.WithDocumentText(docId, text);
                changedProjects.Add(docId.ProjectId);
            }

            if (changedProjects.Count == 0)
                return;

            var staleIds = new HashSet<ProjectId>(changedProjects);
            var graph = solution.GetProjectDependencyGraph();
            foreach (var projectId in changedProjects)
                staleIds.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(projectId));

            await RecompileAndSwapAsync(solution, staleIds).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] Committed {documents.Count} written document(s) to the in-memory snapshot.")
                .ConfigureAwait(false);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>
    /// Shared tail of the in-place update paths (#282 pattern): recompiles the stale projects of
    /// an already-updated solution fork (unchanged projects keep their carried-over compilations,
    /// preserving symbol identity), swaps in the new LoadedSolution + resolvers under
    /// <see cref="_lock"/>, and re-maps the tracker so future edits resolve against the new
    /// authoritative snapshot. Callers must hold <see cref="_rebuildGate"/>.
    /// </summary>
    private async Task RecompileAndSwapAsync(Solution solution, IReadOnlySet<ProjectId> staleIds)
    {
        var compilations = new ConcurrentDictionary<ProjectId, Compilation>(_loaded.Compilations);
        var staleProjects = solution.Projects.Where(p => staleIds.Contains(p.Id)).ToList();
        var tasks = staleProjects.Select(async project =>
        {
            await Console.Error.WriteLineAsync($"[roslyn-codelens] Recompiling: {project.Name}").ConfigureAwait(false);
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation != null)
                compilations[project.Id] = compilation;
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var newLoaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations,
            SkippedProjects = _loaded.SkippedProjects,
            LoadDiagnostics = _loaded.LoadDiagnostics
        };
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        lock (_lock)
        {
            _loaded = newLoaded;
            _resolver = newResolver;
            _metadataResolver = newMetadataResolver;
        }

        // Re-map file->project/document lookups against the new snapshot. Ids are preserved,
        // but a fresh snapshot instance is now authoritative and future edits resolve against it.
        _tracker?.UpdateMappings(newLoaded);
    }

    /// <summary>
    /// Re-opens the solution from disk and recompiles every project. Used for structural
    /// changes (project files, added/removed files) that require MSBuild re-evaluation, and
    /// as the escalation path when an incremental edit can't be applied. Recompiling all
    /// projects is required for correctness: a freshly opened solution has new ProjectIds, so
    /// the old compilations cannot be mixed with it (mixing them was the #282 silent-empty bug).
    /// </summary>
    private async Task RebuildViaFullReloadAsync()
    {
        var solutionLoader = new SolutionLoader();

        // Reuse this solution's existing shadow-copy analyzer loader so generators load from
        // shadow copies (issue #254) instead of re-locking bin\Debug, and the returned solution
        // stays remapped. Snapshot under _lock in case a concurrent reload swapped it. Do NOT
        // dispose it here: its already-acquired analyzers are shared via the process-wide cache.
        ShadowCopyAnalyzerAssemblyLoader? analyzerLoader;
        lock (_lock) { analyzerLoader = _analyzerLoader; }

        var open = await solutionLoader.OpenAsync(_solutionPath!, null, analyzerLoader).ConfigureAwait(false);
        var solution = open.Solution;
        var workspace = open.Workspace;
        var compilations = await solutionLoader.CompileAllParallelAsync(solution).ConfigureAwait(false);

        var newLoaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations,
            SkippedProjects = open.Skipped,
            LoadDiagnostics = open.LoadDiagnostics
        };
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        Workspace? oldWorkspace;
        lock (_lock)
        {
            _loaded = newLoaded;
            _resolver = newResolver;
            _metadataResolver = newMetadataResolver;
            oldWorkspace = _workspace;
            _workspace = workspace;
        }

        _tracker!.UpdateMappings(newLoaded);

        // Dispose the previous workspace AFTER the swap (fixes a pre-existing leak: each stale rebuild
        // otherwise leaks a workspace + its out-of-process BuildHost). The analyzer loader is REUSED, not disposed.
        oldWorkspace?.Dispose();

        await Console.Error.WriteLineAsync("[roslyn-codelens] Rebuild complete.").ConfigureAwait(false);
    }

    private static async Task<Microsoft.CodeAnalysis.Text.SourceText?> TryReadSourceTextAsync(string path)
    {
        // The file may still be locked by the editor that just wrote it; a couple of short
        // retries clear that. A genuine failure (deleted, access denied) returns null so the
        // caller escalates to a full reload rather than applying stale text.
        const int attempts = 3;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Microsoft.CodeAnalysis.Text.SourceText.From(stream);
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Test-only seam: classify a changed path synchronously (bypassing the file watcher's
    /// debounce) so the incremental rebuild path can be exercised deterministically.
    /// </summary>
    internal void SimulateFileChangeForTest(string fullPath) => _tracker?.NotifyChangedPathForTest(fullPath);

    /// <summary>
    /// Rebuilds if stale, then snapshots the loaded solution and both resolvers under one lock
    /// so they are guaranteed to come from the same swap. Tools that need more than one of these
    /// must use this instead of the individual getters: each getter rebuilds independently, so a
    /// watcher-driven rebuild racing between two getter calls could otherwise pair a solution with
    /// a resolver built from a different snapshot — the same identity mismatch behind #282, just in
    /// a narrow window.
    /// </summary>
    public SolutionAnalysisContext GetAnalysisContext()
    {
        ThrowIfLoadFailed();
        _warmupTask?.GetAwaiter().GetResult();
        if (_warmupException != null)
            throw new InvalidOperationException("Solution warmup failed.", _warmupException);
        RebuildIfStale();
        lock (_lock)
            return new SolutionAnalysisContext(_loaded, _resolver, _metadataResolver);
    }

    public async Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync()
    {
        if (_solutionPath == null)
            throw new InvalidOperationException("No solution path configured. Cannot reload.");

        // Hold the rebuild gate for the whole reload: wait for any in-progress auto-rebuild to
        // finish, then keep it out (auto-rebuilds skip and serve current data) so none can clobber
        // our swap or dispose a workspace we still reference.
        await _rebuildGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await ReloadAndSwapAsync().ConfigureAwait(false);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    /// <summary>
    /// Re-opens the solution from disk, recompiles everything, and swaps in the result, disposing
    /// the superseded workspace/loader afterwards. Must be called while holding <see cref="_rebuildGate"/>.
    /// </summary>
    private async Task<(int ProjectCount, TimeSpan Elapsed)> ReloadAndSwapAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var solutionLoader = new SolutionLoader();
        var analyzerLoader = new ShadowCopyAnalyzerAssemblyLoader(SharedCache);
        SolutionLoader.SolutionOpen open;
        try
        {
            open = await solutionLoader.OpenAsync(_solutionPath!, null, analyzerLoader).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            analyzerLoader.Dispose();
            throw new InvalidOperationException(SolutionLoadFailure.Describe(_solutionPath!, ex), ex);
        }
        var solution = open.Solution;
        var workspace = open.Workspace;
        var compilations = await solutionLoader.CompileAllParallelAsync(solution).ConfigureAwait(false);

        var newLoaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations,
            SkippedProjects = open.Skipped,
            LoadDiagnostics = open.LoadDiagnostics
        };
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        ShadowCopyAnalyzerAssemblyLoader? oldLoader;
        Workspace? oldWorkspace;
        lock (_lock)
        {
            _loaded = newLoaded;
            _resolver = newResolver;
            _metadataResolver = newMetadataResolver;
            oldLoader = _analyzerLoader;
            oldWorkspace = _workspace;
            _analyzerLoader = analyzerLoader;
            _workspace = workspace;
        }

        _tracker?.UpdateMappings(newLoaded);
        _tracker?.ClearStale();

        // Dispose the previous workspace/loader AFTER the state swap so nothing in-flight uses them.
        oldWorkspace?.Dispose();
        oldLoader?.Dispose();

        sw.Stop();
        return (newLoaded.Compilations.Count, sw.Elapsed);
    }

    public void Dispose()
    {
        // Idempotent: snapshot the swappable state under _lock (ForceReloadAsync swaps
        // _workspace/_analyzerLoader under the same lock), then dispose outside it. This
        // avoids a double release of refcounted analyzer handles that could prematurely
        // unload another live solution's analyzer ALC.
        Workspace? workspace;
        ShadowCopyAnalyzerAssemblyLoader? analyzerLoader;
        FileChangeTracker? tracker;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            workspace = _workspace;
            analyzerLoader = _analyzerLoader;
            tracker = _tracker;
            _workspace = null;
            _analyzerLoader = null;
        }
        tracker?.Dispose();
        _peCache.Dispose();
        workspace?.Dispose();
        analyzerLoader?.Dispose();   // releases each acquired analyzer -> refcount--, unload at zero
    }
}
