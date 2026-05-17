using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
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
    private volatile bool _rebuilding;
    private Task? _warmupTask;
    private Exception? _warmupException;
    private readonly Exception? _loadException;
    private readonly string? _loadFailureMessage;
    private readonly PEFileCache _peCache = new();
    private readonly IlDisassemblerAdapter _ilAdapter;

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

    public static async Task<SolutionManager> CreateAsync(string solutionPath)
    {
        var loader = new SolutionLoader();
        Solution solution;
        IReadOnlyList<SkippedProject> skipped;
        try
        {
            (solution, _, skipped) = await loader.OpenAsync(solutionPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"[roslyn-codelens] {SolutionLoadFailure.Describe(solutionPath, ex)}").ConfigureAwait(false);
            return new SolutionManager(solutionPath, ex);
        }

        var emptyLoaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(),
            SkippedProjects = skipped
        };

        var manager = new SolutionManager(emptyLoaded, solutionPath);
        manager._warmupTask = manager.WarmupAsync(loader, solution, skipped);
        return manager;
    }

    public static SolutionManager CreateEmpty()
    {
        return new SolutionManager(LoadedSolution.Empty, null);
    }

    private async Task WarmupAsync(SolutionLoader loader, Solution solution, IReadOnlyList<SkippedProject> skipped)
    {
        try
        {
            await Console.Error.WriteLineAsync("[roslyn-codelens] Background compilation starting...").ConfigureAwait(false);
            var compilations = await loader.CompileAllParallelAsync(solution).ConfigureAwait(false);

            var newLoaded = new LoadedSolution
            {
                Solution = solution,
                Compilations = compilations,
                SkippedProjects = skipped
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
                "No .sln file found. Either run from a directory containing a .sln/.slnx file, " +
                "or pass the solution path as argument: roslyn-codelens-mcp /path/to/Solution.sln");
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
            $"[roslyn-codelens] Rebuilding {staleIds.Count} stale project(s)...");

        try
        {
            RebuildStaleProjects(staleIds).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[roslyn-codelens] Rebuild failed: {ex}. Using cached data.");
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
        var loader = new SolutionLoader();
        var (solution, _, skipped) = await loader.OpenAsync(_solutionPath!).ConfigureAwait(false);
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
            SkippedProjects = skipped
        };
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        lock (_lock)
        {
            _loaded = newLoaded;
            _resolver = newResolver;
            _metadataResolver = newMetadataResolver;
        }

        _tracker!.UpdateMappings(newLoaded);

        await Console.Error.WriteLineAsync("[roslyn-codelens] Rebuild complete.").ConfigureAwait(false);
    }

    public async Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync()
    {
        if (_solutionPath == null)
            throw new InvalidOperationException("No solution path configured. Cannot reload.");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var loader = new SolutionLoader();
        Solution solution;
        IReadOnlyList<SkippedProject> skipped;
        try
        {
            (solution, _, skipped) = await loader.OpenAsync(_solutionPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(SolutionLoadFailure.Describe(_solutionPath, ex), ex);
        }
        var compilations = await loader.CompileAllParallelAsync(solution).ConfigureAwait(false);

        var newLoaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations,
            SkippedProjects = skipped
        };
        var newResolver = new SymbolResolver(newLoaded);
        var newMetadataResolver = new MetadataSymbolResolver(newLoaded, newResolver);

        lock (_lock)
        {
            _loaded = newLoaded;
            _resolver = newResolver;
            _metadataResolver = newMetadataResolver;
        }

        _tracker?.UpdateMappings(newLoaded);
        _tracker?.ClearStale();

        sw.Stop();
        return (newLoaded.Compilations.Count, sw.Elapsed);
    }

    public void Dispose()
    {
        _tracker?.Dispose();
        _peCache.Dispose();
    }
}
