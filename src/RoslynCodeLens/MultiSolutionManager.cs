namespace RoslynCodeLens;

public sealed class MultiSolutionManager : IDisposable
{
    private readonly Dictionary<string, SolutionManager> _managers;
    private string? _activeKey;

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

    private SolutionManager Active =>
        _activeKey != null && _managers.TryGetValue(_activeKey, out var m)
            ? m
            : SolutionManager.CreateEmpty();

    public void EnsureLoaded() => Active.EnsureLoaded();
    public LoadedSolution GetLoadedSolution() => Active.GetLoadedSolution();
    public SymbolResolver GetResolver() => Active.GetResolver();
    public Task WaitForWarmupAsync() => Active.WaitForWarmupAsync();
    public Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync() => Active.ForceReloadAsync();

    public void Dispose()
    {
        foreach (var m in _managers.Values)
            m.Dispose();
    }
}
