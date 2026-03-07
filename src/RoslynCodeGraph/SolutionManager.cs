using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeGraph;

public class SolutionManager : IDisposable
{
    private LoadedSolution _loaded;
    private SymbolResolver _resolver;
    private readonly string? _solutionPath;
    private readonly FileChangeTracker? _tracker;
    private readonly object _lock = new();

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
        var loaded = await loader.LoadAsync(solutionPath);
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
            if (!_tracker.HasStaleProjects)
                return;

            var staleIds = _tracker.StaleProjectIds;
            Console.Error.WriteLine(
                $"[roslyn-codegraph] Rebuilding {staleIds.Count} stale project(s)...");

            try
            {
                RebuildStaleProjects(staleIds).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[roslyn-codegraph] Rebuild failed: {ex.Message}. Using cached data.");
            }

            _tracker.ClearStale();
        }
    }

    private async Task RebuildStaleProjects(IReadOnlySet<ProjectId> staleIds)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
            Console.Error.WriteLine($"[roslyn-codegraph] Warning: {e.Diagnostic.Message}");

        var solution = await workspace.OpenSolutionAsync(_solutionPath!);
        var compilations = new Dictionary<ProjectId, Compilation>(_loaded.Compilations);

        foreach (var project in solution.Projects)
        {
            if (!staleIds.Contains(project.Id))
                continue;

            Console.Error.WriteLine($"[roslyn-codegraph] Recompiling: {project.Name}");
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
                compilations[project.Id] = compilation;
        }

        _loaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations
        };
        _resolver = new SymbolResolver(_loaded);
        _tracker!.UpdateMappings(_loaded);

        Console.Error.WriteLine("[roslyn-codegraph] Rebuild complete.");
    }

    public void Dispose()
    {
        _tracker?.Dispose();
    }
}
