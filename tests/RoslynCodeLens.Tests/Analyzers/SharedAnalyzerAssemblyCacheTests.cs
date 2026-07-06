using System.Runtime.CompilerServices;
using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Tests.Analyzers;

public class SharedAnalyzerAssemblyCacheTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rcl-cache-test", Guid.NewGuid().ToString("N"));

    public SharedAnalyzerAssemblyCacheTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ } }

    // Copy an arbitrary real assembly to act as a stand-in "analyzer" we are allowed to delete.
    private string MakeFakeAnalyzer(string name)
    {
        var src = typeof(System.Text.Json.JsonSerializer).Assembly.Location;
        var dst = Path.Combine(_tmp, name + ".dll");
        File.Copy(src, dst, overwrite: true);
        return dst;
    }

    [Fact]
    public void Acquire_LoadsFromShadowCopy_NotOriginalPath()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Alpha");

        var (asm, _) = cache.Acquire(original, Array.Empty<string>());

        Assert.NotNull(asm);
        Assert.NotEqual(
            Path.GetFullPath(original),
            Path.GetFullPath(asm.Location),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acquire_LeavesOriginalFileUnlocked()   // THE #254 regression guard
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Beta");

        cache.Acquire(original, Array.Empty<string>());

        // If the original were locked (the bug), this throws IOException.
        var ex = Record.Exception(() => File.Delete(original));
        Assert.Null(ex);
    }

    [Fact]
    public void Acquire_SamePathTwice_DedupsToSameAssembly()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Gamma");

        var (a, _) = cache.Acquire(original, Array.Empty<string>());
        var (b, _) = cache.Acquire(original, Array.Empty<string>());

        Assert.Same(a, b);
    }

    [Fact]
    public void Release_UnloadsOnlyWhenRefCountReachesZero()
    {
        var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Delta");
        var (root, handle1) = cache.Acquire(original, Array.Empty<string>());
        var shadowDirBefore = Path.GetDirectoryName(root.Location)!;
        var (_, handle2) = cache.Acquire(original, Array.Empty<string>());   // refcount = 2

        cache.Release(handle1);                            // -> 1, still present
        Assert.True(Directory.Exists(shadowDirBefore));

        cache.Release(handle2);                            // -> 0, entry unloaded + removed
        ForceGc();

        // The collectible ALC's unload is best-effort: on Windows the shadow DLL can stay
        // memory-mapped past GC, so we cannot reliably assert the directory is gone. What we
        // CAN prove is that the entry left the cache at refcount zero: a further Release is a
        // harmless no-op, and re-Acquire has to build a brand-new shadow dir (a live entry
        // would have deduped and returned the same path).
        var noop = Record.Exception(() => cache.Release(handle2));
        Assert.Null(noop);

        var (reAsm, _) = cache.Acquire(original, Array.Empty<string>());
        var shadowDirAfter = Path.GetDirectoryName(reAsm.Location)!;
        Assert.NotEqual(shadowDirBefore, shadowDirAfter, StringComparer.OrdinalIgnoreCase);
        cache.Dispose();
    }

    [Fact]
    public void Release_AfterOriginalDeleted_DoesNotThrow()   // guards handle-based release (#254)
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFakeAnalyzer("Epsilon");

        var (_, handle) = cache.Acquire(original, Array.Empty<string>());
        File.Delete(original);   // simulate the concurrent build deleting the DLL

        var ex = Record.Exception(() => cache.Release(handle));
        Assert.Null(ex);         // must not throw FileNotFoundException
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGc()
    {
        for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    }
}
