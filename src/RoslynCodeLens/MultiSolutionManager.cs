using System.Collections.Concurrent;
using RoslynCodeLens.Metadata;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens;

public sealed class MultiSolutionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SolutionManager> _managers;
    private string? _activeKey;
    private readonly Lock _lock = new();

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

    public void EnsureLoaded() => Active.EnsureLoaded();
    public LoadedSolution GetLoadedSolution() => Active.GetLoadedSolution();
    public SymbolResolver GetResolver() => Active.GetResolver();
    public MetadataSymbolResolver GetMetadataResolver() => Active.GetMetadataResolver();
    public IlDisassemblerAdapter GetIlDisassembler() => Active.GetIlDisassembler();
    public Task WaitForWarmupAsync() => Active.WaitForWarmupAsync();
    public Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync() => Active.ForceReloadAsync();

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

    public IReadOnlyList<SkippedProjectInfo> GetActiveSkippedProjects()
    {
        try
        {
            return Active.GetLoadedSolution().SkippedProjects
                .Select(s => new SkippedProjectInfo(s.Name, s.Path, s.Kind, s.Reason))
                .ToList();
        }
        catch
        {
            return Array.Empty<SkippedProjectInfo>();
        }
    }

    public string SetActiveSolution(string name)
    {
        var matches = _managers.Keys
            .Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new McpToolException(
                ToolErrorCode.ProjectNotFound,
                $"No solution matching '{name}'. Available: {string.Join(", ", _managers.Keys.Select(Path.GetFileName))}",
                new { name, available = _managers.Keys.Select(Path.GetFileName).ToArray() });

        if (matches.Count > 1)
            throw new McpToolException(
                ToolErrorCode.AmbiguousMatch,
                $"Ambiguous match for '{name}'. Matches: {string.Join(", ", matches)}",
                new { name, matches });

        lock (_lock) { _activeKey = matches[0]; }
        return matches[0];
    }

    /// <summary>
    /// Load a new solution at runtime. If it's already loaded with no (seeded) filter,
    /// just activates it. If it's already loaded and a seeded <paramref name="filter"/> is
    /// supplied, the previous workspace is disposed and reloaded fresh so the new filter
    /// takes effect (replace semantics — no coexisting filtered views).
    /// Returns the normalised path of the loaded solution.
    /// </summary>
    public async Task<string> LoadSolutionAsync(string solutionPath, ProjectFilter? filter = null)
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
                _activeKey = normalised;
                return normalised;
            }
        }

        // Dispose the replaced manager OUTSIDE the lock to avoid blocking other callers.
        toDispose?.Dispose();

        var manager = await SolutionManager.CreateAsync(normalised, filter).ConfigureAwait(false);

        if (manager.HasLoadFailure)
        {
            var message = manager.LoadFailureMessage!;
            manager.Dispose();
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
            _activeKey = normalised;
        }

        return normalised;
    }

    /// <summary>
    /// Unload a solution by partial name match, freeing its memory.
    /// If the unloaded solution is currently active, another loaded solution (if any)
    /// will become active; otherwise there will be no active solution.
    /// </summary>
    public string UnloadSolution(string name)
    {
        SolutionManager? managerToDispose = null;
        string key;

        lock (_lock)
        {
            var keysSnapshot = _managers.Keys.ToList();

            var matches = keysSnapshot
                .Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                throw new McpToolException(
                    ToolErrorCode.ProjectNotFound,
                    $"No solution matching '{name}'. Available: {string.Join(", ", keysSnapshot.Select(Path.GetFileName))}",
                    new { name, available = keysSnapshot.Select(Path.GetFileName).ToArray() });

            if (matches.Count > 1)
                throw new McpToolException(
                    ToolErrorCode.AmbiguousMatch,
                    $"Ambiguous match for '{name}'. Matches: {string.Join(", ", matches)}",
                    new { name, matches });

            key = matches[0];

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
