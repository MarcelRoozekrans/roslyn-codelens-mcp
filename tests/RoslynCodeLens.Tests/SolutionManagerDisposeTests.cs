using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class SolutionManagerDisposeTests
{
    [Fact]
    public async Task ForceReloadAsync_AfterDispose_ThrowsObjectDisposed()
    {
        // A queued rebuild keeps the manager it was queued for. If that solution is unloaded first,
        // the rebuild must fail, not open a new workspace into a disposed manager and report success.
        var dir = Directory.CreateTempSubdirectory("codelens-606-");
        try
        {
            var bad = Path.Combine(dir.FullName, "Bad.sln");
            await File.WriteAllTextAsync(bad, "not a solution file");
            var manager = await SolutionManager.CreateAsync(bad);
            manager.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ForceReloadAsync());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReloadAndSwapAsync_DisposedDuringReload_ThrowsInsteadOfSwapping()
    {
        // Drives the swap directly, past the check ForceReloadAsync makes first, which is the state a
        // dispose that lands while the solution is being reopened leaves behind.
        var dir = Directory.CreateTempSubdirectory("codelens-606-");
        try
        {
            var bad = Path.Combine(dir.FullName, "Bad.sln");
            await File.WriteAllTextAsync(bad, "not a solution file");
            var manager = await SolutionManager.CreateAsync(bad);
            manager.Dispose();

            var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ReloadAndSwapAsync());
            Assert.Contains("unloaded while it was being rebuilt", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
