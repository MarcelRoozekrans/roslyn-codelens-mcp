using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens;

public sealed class MultiSolutionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SolutionManager> _managers;
    private string? _activeKey;
    private readonly Lock _lock = new();

    // Runs after an unload, once the unloaded manager is out of reach. Replaceable so a test can observe it
    // without forcing a collection.
    internal Action MemoryReleaser { get; set; } = MemoryRelease.CollectAndDecommit;

    private MultiSolutionManager(ConcurrentDictionary<string, SolutionManager> managers, string? activeKey)
    {
        _managers = managers;
        _activeKey = activeKey;
    }

    public static async Task<MultiSolutionManager> CreateAsync(IReadOnlyList<string> solutionPaths)
    {
        if (solutionPaths.Count == 0)
            return CreateEmpty();

        var managers = new ConcurrentDictionary<string, SolutionManager>(StringComparer.OrdinalIgnoreCase);
        string? firstSuccessfulKey = null;
        foreach (var path in solutionPaths)
        {
            var normalised = Path.GetFullPath(path);
            if (managers.ContainsKey(normalised))
                continue;

            SolutionManager manager;
            try
            {
                manager = await SolutionManager.CreateAsync(normalised).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"[roslyn-codelens] Skipping solution '{Path.GetFileName(normalised)}': {ex.Message}").ConfigureAwait(false);
                continue;
            }

            managers[normalised] = manager;
            if (firstSuccessfulKey == null && !manager.HasLoadFailure)
                firstSuccessfulKey = normalised;
        }

        var activeKey = firstSuccessfulKey ?? (managers.IsEmpty ? null : Path.GetFullPath(solutionPaths[0]));
        return new MultiSolutionManager(managers, activeKey);
    }

    /// <summary>Builds a manager over already-created solutions, for tests that need no file watcher.</summary>
    internal static MultiSolutionManager CreateForTesting(
        IReadOnlyDictionary<string, SolutionManager> managers, string? activeKey) =>
        new(new ConcurrentDictionary<string, SolutionManager>(managers, StringComparer.OrdinalIgnoreCase), activeKey);

    public static MultiSolutionManager CreateEmpty() =>
        new(new ConcurrentDictionary<string, SolutionManager>(StringComparer.OrdinalIgnoreCase), null);

    private SolutionManager Active
    {
        get
        {
            string? key;
            lock (_lock) { key = _activeKey; }
            if (key == null || !_managers.TryGetValue(key, out var m))
                throw new McpToolException(
                    ToolErrorCode.InvalidArgument,
                    key == null
                        ? "No solution loaded. Pass a .sln/.slnx path as argument."
                        : $"Active solution key '{key}' not found in loaded solutions.",
                    new { activeKey = key });
            return m;
        }
    }

    /// <summary>
    /// The solution a single tool call runs against. <paramref name="solution"/> is matched like
    /// <see cref="SetActiveSolution"/>; null, empty or whitespace means the active solution, read once.
    /// A tool resolves once and uses the result for the whole call, so a concurrent
    /// <c>set_active_solution</c> or <c>load_solution</c> from another caller cannot switch it mid-call.
    /// A named solution never falls back to the active one.
    /// </summary>
    public SolutionManager Resolve(string? solution)
    {
        if (string.IsNullOrWhiteSpace(solution))
            return Active;

        var key = SolutionMatcher.Match(_managers.Keys.ToList(), solution);
        if (!_managers.TryGetValue(key, out var manager))
            throw new McpToolException(
                ToolErrorCode.ProjectNotFound,
                $"Solution '{key}' was unloaded while resolving '{solution}'.",
                new { name = solution });
        return manager;
    }

    public void EnsureLoaded() => Active.EnsureLoaded();
    public LoadedSolution GetLoadedSolution() => Active.GetLoadedSolution();
    public SymbolResolver GetResolver() => Active.GetResolver();
    public MetadataSymbolResolver GetMetadataResolver() => Active.GetMetadataResolver();

    /// <summary>
    /// An internally-consistent (solution, resolver, metadata) snapshot from the active solution.
    /// Tools needing more than one of these must use this rather than the individual getters,
    /// which each rebuild independently and can diverge if a rebuild races between two calls.
    /// </summary>
    public SolutionAnalysisContext GetAnalysisContext() => Active.GetAnalysisContext();
    public IlDisassemblerAdapter GetIlDisassembler() => Active.GetIlDisassembler();
    public Task WaitForWarmupAsync() => Active.WaitForWarmupAsync();
    public Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync() => Active.ForceReloadAsync();

    /// <summary>
    /// Commits already-written document texts to the active solution's in-memory snapshot
    /// immediately (bypassing the file watcher's debounce). See
    /// <see cref="SolutionManager.CommitDocumentTextsAsync"/>.
    /// </summary>
    public Task CommitDocumentTextsAsync(
        IReadOnlyList<(Microsoft.CodeAnalysis.DocumentId Id, Microsoft.CodeAnalysis.Text.SourceText Text)> documents,
        CancellationToken ct = default)
        => Active.CommitDocumentTextsAsync(documents, ct);

    public IReadOnlyList<SolutionInfo> ListSolutions()
    {
        string? activeKey;
        lock (_lock) { activeKey = _activeKey; }

        return _managers
            .Select(kvp =>
            {
                var m = kvp.Value;
                int projectCount = 0;
                string status;
                IReadOnlyList<SkippedProjectInfo> skipped = Array.Empty<SkippedProjectInfo>();
                if (m.HasLoadFailure)
                {
                    status = "error";
                }
                else
                {
                    try
                    {
                        var loaded = m.GetLoadedSolution();
                        projectCount = loaded.Compilations.Count;
                        status = loaded.IsEmpty ? "empty" : "ready";
                        skipped = loaded.SkippedProjects
                            .Select(s => new SkippedProjectInfo(s.Name, s.Path, s.Kind, s.Reason))
                            .ToList();
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("warmup failed", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "error";
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[roslyn-codelens] Error reading solution status ({kvp.Key}): {ex}");
                        status = "unknown";
                    }
                }
                return new SolutionInfo(kvp.Key, string.Equals(kvp.Key, activeKey, StringComparison.Ordinal), projectCount, status, skipped);
            })
            .ToList();
    }

    public IReadOnlyList<SkippedProjectInfo> GetActiveSkippedProjects() => GetSkippedProjects(null);

    /// <summary>Projects the loader skipped in the given solution (see <see cref="Resolve"/>); empty if it cannot be read.</summary>
    public IReadOnlyList<SkippedProjectInfo> GetSkippedProjects(string? solution)
    {
        try
        {
            return Resolve(solution).GetLoadedSolution().SkippedProjects
                .Select(s => new SkippedProjectInfo(s.Name, s.Path, s.Kind, s.Reason))
                .ToList();
        }
        catch
        {
            return Array.Empty<SkippedProjectInfo>();
        }
    }

    /// <summary>
    /// Reference-resolution failures on the active solution (projects that opened with
    /// dropped references). Non-empty means results from those projects may be incomplete.
    /// </summary>
    public IReadOnlyList<string> GetActiveLoadDiagnostics() => GetLoadDiagnostics(null);

    /// <summary>Reference-resolution failures on the given solution (see <see cref="Resolve"/>).</summary>
    public IReadOnlyList<string> GetLoadDiagnostics(string? solution)
    {
        try
        {
            return Resolve(solution).GetLoadedSolution().LoadDiagnostics;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public string SetActiveSolution(string name)
    {
        var key = SolutionMatcher.Match(_managers.Keys.ToList(), name);
        lock (_lock) { _activeKey = key; }
        return key;
    }

    /// <summary>
    /// Load a new solution at runtime. If it's already loaded with no (seeded) filter,
    /// just activates it. If it's already loaded and a seeded <paramref name="filter"/> is
    /// supplied, the previous workspace is disposed and reloaded fresh so the new filter
    /// takes effect (replace semantics — no coexisting filtered views).
    /// Returns the normalised path of the loaded solution.
    /// With <paramref name="activate"/> false the active solution stays as it is (unless there is none),
    /// so a caller that passes <c>solution</c> per call does not switch it for everyone else.
    /// </summary>
    public async Task<string> LoadSolutionAsync(string solutionPath, ProjectFilter? filter = null, bool activate = true)
    {
        var normalised = Path.GetFullPath(solutionPath);

        SolutionManager? toDispose = null;
        lock (_lock)
        {
            if (_managers.TryGetValue(normalised, out var existing) && filter is not null && filter.HasSeeds)
            {
                // Replace semantics: a filtered re-load disposes the previous workspace
                // so the new filter takes effect rather than silently re-activating the old view.
                _managers.TryRemove(normalised, out _);
                toDispose = existing;
            }
            else if (_managers.ContainsKey(normalised))
            {
                // No-filter (or seedless filter) re-load of an already-loaded path → re-activate fast path.
                _activeKey = NextActiveKey(_activeKey, normalised, activate);
                return normalised;
            }
        }

        // Dispose the replaced manager OUTSIDE the lock to avoid blocking other callers.
        toDispose?.Dispose();

        SolutionManager manager;
        try
        {
            manager = await SolutionManager.CreateAsync(normalised, filter).ConfigureAwait(false);
        }
        catch
        {
            DropDanglingActiveKey();
            throw;
        }

        if (manager.HasLoadFailure)
        {
            var message = manager.LoadFailureMessage!;
            manager.Dispose();
            // A failed filtered re-load has already dropped the previous workspace; the active key
            // must not keep pointing at it.
            DropDanglingActiveKey();
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                message,
                new { solutionPath = normalised, reason = message });
        }

        lock (_lock)
        {
            // Double-check: another concurrent call may have loaded this solution while we were creating the manager.
            if (_managers.ContainsKey(normalised))
            {
                manager.Dispose();
            }
            else
            {
                _managers[normalised] = manager;
            }
            _activeKey = NextActiveKey(_activeKey, normalised, activate);
        }

        return normalised;
    }

    /// <summary>
    /// Moves the active key off a solution that is no longer loaded, the same way unloading the active
    /// solution does: to another loaded solution if there is one, otherwise to none.
    /// </summary>
    private void DropDanglingActiveKey()
    {
        lock (_lock)
        {
            if (_activeKey is not null && !_managers.ContainsKey(_activeKey))
                _activeKey = _managers.Keys.FirstOrDefault();
        }
    }

    /// <summary>
    /// The active key after loading <paramref name="loaded"/>. Without activation the current key stays,
    /// except when nothing is active: no caller can depend on an empty key, and the server would
    /// otherwise have a loaded solution but answer "No solution loaded" to every call without one.
    /// </summary>
    internal static string? NextActiveKey(string? current, string loaded, bool activate) =>
        activate || current is null ? loaded : current;

    /// <summary>
    /// Unload a solution by partial name match, freeing its memory.
    /// If the unloaded solution is currently active, another loaded solution (if any)
    /// will become active; otherwise there will be no active solution.
    /// The memory is handed back to the operating system afterwards (#595).
    /// </summary>
    public string UnloadSolution(string name)
    {
        var key = UnloadAndDispose(name);

        // A separate method, so the disposed manager is no longer a live local when the collection runs.
        MemoryReleaser();
        return key;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string UnloadAndDispose(string name)
    {
        SolutionManager? managerToDispose = null;
        string key;

        lock (_lock)
        {
            var keysSnapshot = _managers.Keys.ToList();

            key = SolutionMatcher.Match(keysSnapshot, name);

            if (string.Equals(_activeKey, key, StringComparison.OrdinalIgnoreCase))
            {
                var remaining = keysSnapshot
                    .Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _activeKey = remaining.Count > 0 ? remaining[0] : null;
            }

            if (_managers.TryRemove(key, out var manager))
                managerToDispose = manager;
        }

        managerToDispose?.Dispose();
        return key;
    }

    public void Dispose()
    {
        foreach (var m in _managers.Values)
            m.Dispose();
    }
}
