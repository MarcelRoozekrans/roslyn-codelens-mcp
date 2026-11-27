using System.Runtime;

namespace RoslynCodeLens;

/// <summary>
/// Returns the memory of a solution that was just unloaded to the operating system. Disposing the
/// workspace drops the references, but the GC keeps the freed pages committed and an idle server runs
/// no full collection, so the private bytes would stay at their peak until a restart (#595).
/// </summary>
internal static class MemoryRelease
{
    /// <summary>
    /// One full blocking collection that compacts the heap and decommits freed memory. A second pass
    /// after the finalizers ran collects what only they released, for example memory-mapped metadata
    /// and unloaded analyzer load contexts. Meant for rare, user-initiated points such as an unload.
    /// </summary>
    public static void CollectAndDecommit()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // CompactOnce resets itself after the next blocking collection.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
