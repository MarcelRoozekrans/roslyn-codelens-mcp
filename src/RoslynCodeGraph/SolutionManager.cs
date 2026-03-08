using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeGraph;

public sealed class SolutionManager : IDisposable
{
    private LoadedSolution _loaded;
    private SymbolResolver _resolver;
    private readonly string? _solutionPath;
    private readonly FileChangeTracker? _tracker;
    private readonly Lock _lock = new();
    private volatile bool _rebuilding;

    private SolutionManager(LoadedSolution loaded, string? solutionPath)
    {
        _loaded = loaded;
        _solutionPath = solutionPath;
        _resolver = new SymbolResolver(loaded);

        if (solutionPath != null && !loaded.IsEmpty)
        {
            _tracker = new FileChangeTracker(loaded, solutionPath);
        }
    }

    public static async Task<SolutionManager> CreateAsync(string solutionPath)
    {
        var loader = new SolutionLoader();
        var loaded = await loader.LoadAsync(solutionPath).ConfigureAwait(false);
        return new SolutionManager(loaded, solutionPath);
    }

    public static SolutionManager CreateEmpty()
    {
        return new SolutionManager(LoadedSolution.Empty, null);
    }

    public LoadedSolution GetLoadedSolution()
    {
        RebuildIfStale();
        return _loaded;
    }

    public SymbolResolver GetResolver()
    {
        RebuildIfStale();
        return _resolver;
    }

    public void EnsureLoaded()
    {
        if (_loaded.IsEmpty)
            throw new InvalidOperationException(
                "No .sln file found. Either run from a directory containing a .sln/.slnx file, " +
                "or pass the solution path as argument: roslyn-codegraph-mcp /path/to/Solution.sln");
    }

    private void RebuildIfStale()
    {
        if (_tracker == null || !_tracker.HasStaleProjects)
            return;

        lock (_lock)
        {
            // Double-check after acquiring lock
            if (_tracker == null || !_tracker.HasStaleProjects || _rebuilding)
                return;

            _rebuilding = true;
        }

        // Rebuild outside the lock to avoid blocking other reads
        var staleIds = _tracker.GetStaleProjectIds();
        Console.Error.WriteLine(
            $"[roslyn-codegraph] Rebuilding {staleIds.Count} stale project(s)...");

        try
        {
            RebuildStaleProjects(staleIds).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[roslyn-codegraph] Rebuild failed: {ex}. Using cached data.");
        }
        finally
        {
            lock (_lock)
            {
                _rebuilding = false;
            }
            _tracker.ClearStale();
        }
    }

    private async Task RebuildStaleProjects(IReadOnlySet<ProjectId> staleIds)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
            Console.Error.WriteLine($"[roslyn-codegraph] Warning: {e.Diagnostic.Message}");

        var solution = await workspace.OpenSolutionAsync(_solutionPath!).ConfigureAwait(false);
        var compilations = new Dictionary<ProjectId, Compilation>(_loaded.Compilations);

        foreach (var project in solution.Projects)
        {
            if (!staleIds.Contains(project.Id))
                continue;

            await Console.Error.WriteLineAsync($"[roslyn-codegraph] Recompiling: {project.Name}").ConfigureAwait(false);
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            if (compilation != null)
                compilations[project.Id] = compilation;
        }

        var newLoaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations
        };
        var newResolver = new SymbolResolver(newLoaded);

        lock (_lock)
        {
            _loaded = newLoaded;
            _resolver = newResolver;
        }

        _tracker!.UpdateMappings(newLoaded);

        await Console.Error.WriteLineAsync("[roslyn-codegraph] Rebuild complete.").ConfigureAwait(false);
    }

    public async Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync()
    {
        if (_solutionPath == null)
            throw new InvalidOperationException("No solution path configured. Cannot reload.");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var loader = new SolutionLoader();
        var newLoaded = await loader.LoadAsync(_solutionPath).ConfigureAwait(false);
        var newResolver = new SymbolResolver(newLoaded);

        lock (_lock)
        {
            _loaded = newLoaded;
            _resolver = newResolver;
        }

        _tracker?.UpdateMappings(newLoaded);
        _tracker?.ClearStale();

        sw.Stop();
        return (newLoaded.Compilations.Count, sw.Elapsed);
    }

    public void Dispose()
    {
        _tracker?.Dispose();
    }
}
