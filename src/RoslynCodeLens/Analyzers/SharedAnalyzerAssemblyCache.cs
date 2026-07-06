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
    internal readonly record struct Key(string Path, long Length, DateTime LastWriteUtc);

    /// <summary>
    /// Opaque handle returned by <see cref="Acquire"/> that captures the cache key computed at
    /// acquire time. <see cref="Release"/> takes this handle so it never has to touch the disk —
    /// critical for #254, where a concurrent build may have already deleted or overwritten the
    /// original analyzer DLL by the time we release.
    /// </summary>
    public readonly struct AnalyzerAssemblyHandle
    {
        internal Key Key { get; }
        internal AnalyzerAssemblyHandle(Key key) => Key = key;
    }

    private sealed class Entry
    {
        public required string ShadowDir { get; init; }
        public required AssemblyLoadContext Alc { get; init; }
        public required Assembly Root { get; init; }
        public int RefCount;
    }

    private static readonly string ShadowParent =
        Path.Combine(Path.GetTempPath(), "roslyn-codelens-shadow");

    // Tagged with the process id so a later run can identify (and sweep) roots left by dead processes.
    private readonly string _root =
        Path.Combine(ShadowParent, $"pid-{Environment.ProcessId}-{Guid.NewGuid():N}");
    private readonly ConcurrentDictionary<Key, Entry> _entries = new();
    private readonly Lock _gate = new();
    private bool _disposed;

    public SharedAnalyzerAssemblyCache() => SweepDeadProcessRoots();

    private static Key KeyFor(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        // Normalize path casing so the same DLL referenced with different casing maps to one entry.
        var normalized = Path.GetFullPath(fullPath).ToUpperInvariant();
        return new Key(normalized, fi.Length, fi.LastWriteTimeUtc);
    }

    /// <summary>Load <paramref name="analyzerPath"/> from a shadow copy, incrementing its refcount.</summary>
    /// <param name="dependencyPaths">Sibling dependency locations Roslyn reported for this analyzer.</param>
    /// <returns>The loaded root assembly and an opaque handle to pass back to <see cref="Release"/>.</returns>
    public (Assembly Assembly, AnalyzerAssemblyHandle Handle) Acquire(string analyzerPath, IReadOnlyList<string> dependencyPaths)
    {
        var key = KeyFor(analyzerPath);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                return (existing.Root, new AnalyzerAssemblyHandle(key));
            }

            var shadowDir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            AssemblyLoadContext? alc = null;
            try
            {
                Directory.CreateDirectory(shadowDir);
                var shadowPath = ShadowCopy(analyzerPath, shadowDir);

                alc = new AssemblyLoadContext($"analyzer:{Path.GetFileName(analyzerPath)}", isCollectible: true);
                alc.Resolving += (ctx, name) => ResolveDependency(ctx, name, analyzerPath, dependencyPaths, shadowDir);

                var root = alc.LoadFromAssemblyPath(shadowPath);
                _entries[key] = new Entry { ShadowDir = shadowDir, Alc = alc, Root = root, RefCount = 1 };
                return (root, new AnalyzerAssemblyHandle(key));
            }
            catch
            {
                // A bad image (or any failure) must not leak the collectible ALC or the shadow dir.
                alc?.Unload();
                TryDeleteDir(shadowDir);
                throw;
            }
        }
    }

    /// <summary>
    /// Decrement the refcount for the entry identified by <paramref name="handle"/>; at zero,
    /// unload the ALC and delete the shadow copy. Performs no disk access, so it is safe even if
    /// the original analyzer DLL has since been deleted or overwritten by a concurrent build.
    /// </summary>
    public void Release(AnalyzerAssemblyHandle handle)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(handle.Key, out var entry))
                return;
            if (--entry.RefCount > 0)
                return;

            _entries.TryRemove(handle.Key, out _);
            entry.Alc.Unload();
            // Deliberately do NOT delete the shadow directory here. The just-unloaded assemblies
            // may not be GC-collected yet, so they still appear in AppDomain.CurrentDomain.GetAssemblies()
            // with Location pointing here; code that re-opens loaded assemblies by path (or Roslyn
            // itself) would then hit a missing file. Shadow copies are reclaimed at process exit and
            // swept on next startup (SweepDeadProcessRoots). This matches Roslyn's shadow-copy loader,
            // which keeps its copies for the whole session.
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
        if (File.Exists(dest))
            return dest;

        try
        {
            File.Copy(source, dest, overwrite: false);
        }
        catch (IOException) when (File.Exists(dest))
        {
            // ResolveDependency runs outside _gate (during compilation), so a concurrent resolve
            // may have produced this shadow already. "Already copied" is success.
        }
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
            // Shadow files are intentionally left on disk (see Release) — the loaded assemblies may
            // still be referenced. They are cleaned up by the next process's startup sweep.
        }
    }

    /// <summary>
    /// Best-effort removal of shadow roots left behind by processes that are no longer running.
    /// Runs at construction; never touches this process's own root or any live process's root.
    /// </summary>
    private static void SweepDeadProcessRoots()
    {
        try
        {
            if (!Directory.Exists(ShadowParent))
                return;

            foreach (var dir in Directory.EnumerateDirectories(ShadowParent))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith("pid-", StringComparison.Ordinal))
                    continue;

                var rest = name.AsSpan("pid-".Length);
                var dash = rest.IndexOf('-');
                if (dash <= 0 || !int.TryParse(rest[..dash], out var pid))
                    continue;

                if (pid == Environment.ProcessId || IsProcessAlive(pid))
                    continue;

                try { Directory.Delete(dir, recursive: true); }
                catch { /* another instance may be sweeping too, or files are pinned — best effort */ }
            }
        }
        catch { /* hygiene only; never fail construction over cleanup */ }
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var _ = System.Diagnostics.Process.GetProcessById(pid); return true; }
        catch { return false; }
    }
}
