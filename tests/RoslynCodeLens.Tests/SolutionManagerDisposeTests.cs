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
}
