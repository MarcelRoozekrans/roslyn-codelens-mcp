using RoslynCodeLens.Analyzers;

namespace RoslynCodeLens.Tests.Analyzers;

public class ShadowCopyAnalyzerAssemblyLoaderTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rcl-facade-test", Guid.NewGuid().ToString("N"));
    public ShadowCopyAnalyzerAssemblyLoaderTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private string MakeFake(string n)
    {
        var dst = Path.Combine(_tmp, n + ".dll");
        File.Copy(typeof(System.Text.Json.JsonSerializer).Assembly.Location, dst, true);
        return dst;
    }

    [Fact]
    public void LoadFromPath_ReturnsShadowedAssembly_AndLeavesOriginalUnlocked()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var loader = new ShadowCopyAnalyzerAssemblyLoader(cache);
        var original = MakeFake("Facade1");
        loader.AddDependencyLocation(original);

        var asm = loader.LoadFromPath(original);

        Assert.NotNull(asm);
        Assert.Null(Record.Exception(() => File.Delete(original)));
    }

    [Fact]
    public void Dispose_ReleasesAcquired_SoReacquireIsFresh()
    {
        using var cache = new SharedAnalyzerAssemblyCache();
        var original = MakeFake("Facade2");

        string dir1;
        using (var loader = new ShadowCopyAnalyzerAssemblyLoader(cache))
        {
            loader.AddDependencyLocation(original);
            dir1 = Path.GetDirectoryName(loader.LoadFromPath(original).Location)!;
        }   // Dispose() releases the acquired handle -> entry removed at refcount zero

        // A fresh loader must build a NEW shadow entry (proves the previous one was released,
        // without depending on OS-level shadow-dir deletion, which is best-effort on Windows).
        using var loader2 = new ShadowCopyAnalyzerAssemblyLoader(cache);
        loader2.AddDependencyLocation(original);
        var dir2 = Path.GetDirectoryName(loader2.LoadFromPath(original).Location)!;

        Assert.NotEqual(dir1, dir2);
    }
}
