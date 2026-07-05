using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace RoslynCodeLens.Analyzers;

/// <summary>
/// Per-solution <see cref="IAnalyzerAssemblyLoader"/> that loads analyzers through the process-wide
/// <see cref="SharedAnalyzerAssemblyCache"/> (shadow copies, no bin\Debug lock — issue #254).
/// Keeps the handle from every acquire and releases them all on <see cref="Dispose"/>, so
/// unload_solution can reclaim (best-effort).
/// </summary>
public sealed class ShadowCopyAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader, IDisposable
{
    private readonly SharedAnalyzerAssemblyCache _cache;
    private readonly ConcurrentBag<string> _dependencyLocations = new();
    private readonly ConcurrentBag<SharedAnalyzerAssemblyCache.AnalyzerAssemblyHandle> _handles = new();
    private int _disposed;

    public ShadowCopyAnalyzerAssemblyLoader(SharedAnalyzerAssemblyCache cache) => _cache = cache;

    public void AddDependencyLocation(string fullPath) => _dependencyLocations.Add(fullPath);

    public Assembly LoadFromPath(string fullPath)
    {
        var (assembly, handle) = _cache.Acquire(Path.GetFullPath(fullPath), _dependencyLocations.ToArray());
        _handles.Add(handle);
        return assembly;
    }

    public void Dispose()
    {
        // Release each acquired handle AT MOST ONCE, even under double/raced Dispose —
        // a double release would decrement a shared refcount twice and could prematurely
        // unload another live solution's analyzer ALC.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        foreach (var handle in _handles)
            _cache.Release(handle);
    }
}
