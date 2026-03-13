using RoslynCodeLens.Models;

namespace RoslynCodeLens;

public sealed class MultiSolutionManager : IDisposable
{
    private readonly Dictionary<string, SolutionManager> _managers;
    private string? _activeKey;
    private readonly Lock _lock = new();

    private MultiSolutionManager(Dictionary<string, SolutionManager> managers, string? activeKey)
    {
        _managers = managers;
        _activeKey = activeKey;
    }

    public static async Task<MultiSolutionManager> CreateAsync(IReadOnlyList<string> solutionPaths)
    {
        if (solutionPaths.Count == 0)
            return CreateEmpty();

        var managers = new Dictionary<string, SolutionManager>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in solutionPaths)
        {
            var normalised = Path.GetFullPath(path);
            if (!managers.ContainsKey(normalised))
                managers[normalised] = await SolutionManager.CreateAsync(normalised).ConfigureAwait(false);
        }

        var firstKey = Path.GetFullPath(solutionPaths[0]);
        return new MultiSolutionManager(managers, firstKey);
    }

    public static MultiSolutionManager CreateEmpty() =>
        new([], null);

    private SolutionManager Active
    {
        get
        {
            string? key;
            lock (_lock) { key = _activeKey; }
            if (key == null || !_managers.TryGetValue(key, out var m))
                throw new InvalidOperationException(
                    key == null
                        ? "No solution loaded. Pass a .sln/.slnx path as argument."
                        : $"Active solution key '{key}' not found in loaded solutions.");
            return m;
        }
    }

    public void EnsureLoaded() => Active.EnsureLoaded();
    public LoadedSolution GetLoadedSolution() => Active.GetLoadedSolution();
    public SymbolResolver GetResolver() => Active.GetResolver();
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
                try
                {
                    var loaded = m.GetLoadedSolution();
                    projectCount = loaded.Compilations.Count;
                    status = loaded.IsEmpty ? "empty" : "ready";
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("warmup failed", StringComparison.OrdinalIgnoreCase))
                {
                    status = "error";
                }
                catch
                {
                    status = "unknown";
                }
                return new SolutionInfo(kvp.Key, kvp.Key == activeKey, projectCount, status);
            })
            .ToList();
    }

    public string SetActiveSolution(string name)
    {
        var matches = _managers.Keys
            .Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"No solution matching '{name}'. Available: {string.Join(", ", _managers.Keys.Select(Path.GetFileName))}");

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Ambiguous match for '{name}'. Matches: {string.Join(", ", matches)}");

        lock (_lock) { _activeKey = matches[0]; }
        return matches[0];
    }

    public void Dispose()
    {
        foreach (var m in _managers.Values)
            m.Dispose();
    }
}
