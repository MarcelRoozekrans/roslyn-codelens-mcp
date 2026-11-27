using System.Runtime.CompilerServices;

namespace RoslynCodeLens.Tests;

public class MemoryReleaseTests
{
    [Fact]
    public void CollectAndDecommit_CollectsAnUnreachableObject()
    {
        var weak = AllocateUnreachable();

        MemoryRelease.CollectAndDecommit();

        Assert.False(weak.TryGetTarget(out _));
    }

    // A separate method, so no local of the test still references the object during the collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<byte[]> AllocateUnreachable() => new(new byte[85_000 * 2]);
}
