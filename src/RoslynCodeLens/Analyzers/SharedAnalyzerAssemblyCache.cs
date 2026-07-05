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
