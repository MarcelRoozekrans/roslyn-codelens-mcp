using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tests.Security;

public class TrustStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _trustFile;

    public TrustStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trust-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _trustFile = Path.Combine(_tempDir, "trust.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // Helpers — produce absolute paths that normalize identically on Windows + POSIX.
    private string Sln(params string[] parts) => Path.Combine([_tempDir, .. parts]);
    private string Dir(params string[] parts)
    {
        var combined = Path.Combine([_tempDir, .. parts]);
        return combined.EndsWith(Path.DirectorySeparatorChar) ? combined : combined + Path.DirectorySeparatorChar;
    }

    [Fact]
    public void IsTrusted_EmptyStore_ReturnsFalse()
    {
        var store = new TrustStore(_trustFile);
        Assert.False(store.IsTrusted(Sln("repos", "foo.sln")));
    }

    [Fact]
    public void IsTrusted_AfterAddSession_ReturnsTrue_ButFileNotCreated()
    {
        var store = new TrustStore(_trustFile);
        var path = Sln("repos", "foo.sln");
        store.AddSessionTrust(path);

        Assert.True(store.IsTrusted(path));
        Assert.False(File.Exists(_trustFile));
    }

    [Fact]
    public void IsTrusted_AfterAddPersistent_ReturnsTrue_AndFileWritten()
    {
        var store = new TrustStore(_trustFile);
        var path = Sln("repos", "foo.sln");
        store.AddPersistentTrust(path);

        Assert.True(store.IsTrusted(path));
        Assert.True(File.Exists(_trustFile));

        var reloaded = new TrustStore(_trustFile);
        Assert.True(reloaded.IsTrusted(path));
    }

    [Fact]
    public void IsTrusted_PathUnderTrustedRoot_ReturnsTrue()
    {
        var store = new TrustStore(_trustFile);
        store.AddTrustedRoot(Dir("projects"));

        Assert.True(store.IsTrusted(Sln("projects", "repo", "foo.sln")));
        Assert.True(store.IsTrusted(Sln("projects", "nested", "dir", "bar.sln")));
        Assert.False(store.IsTrusted(Sln("other", "foo.sln")));
    }

    [Fact]
    public void Revoke_RemovesPersistentEntry()
    {
        var store = new TrustStore(_trustFile);
        var path = Sln("repos", "foo.sln");
        store.AddPersistentTrust(path);
        Assert.True(store.IsTrusted(path));

        store.Revoke(path);
        Assert.False(store.IsTrusted(path));
    }

    [Fact]
    public void Revoke_RemovesSessionEntry()
    {
        var store = new TrustStore(_trustFile);
        var path = Sln("repos", "foo.sln");
        store.AddSessionTrust(path);
        store.Revoke(path);
        Assert.False(store.IsTrusted(path));
    }

    [Fact]
    public void PathComparison_IsCaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new TrustStore(_trustFile);
        store.AddSessionTrust("c:\\Repos\\Foo.sln");
        Assert.True(store.IsTrusted("C:\\REPOS\\foo.SLN"));
    }

    [Fact]
    public void List_ReturnsAllEntries()
    {
        var store = new TrustStore(_trustFile);
        var a = Sln("repos", "a.sln");
        var b = Sln("repos", "b.sln");
        var projects = Dir("projects");
        store.AddSessionTrust(a);
        store.AddPersistentTrust(b);
        store.AddTrustedRoot(projects);

        var snapshot = store.GetSnapshot();
        Assert.Contains(snapshot.SessionSolutions, s => s.EndsWith("a.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.PersistentSolutions, s => s.Path.EndsWith("b.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.TrustedRoots, r => r.Contains("projects", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TrustedRoot_DoesNotMatch_SiblingPrefixDirectory()
    {
        // Regression test for prefix-bypass: trusting "<tmp>/projects" must NOT trust "<tmp>/projects-evil/..."
        var store = new TrustStore(_trustFile);
        var root = Path.Combine(_tempDir, "projects"); // no trailing separator — natural user input
        store.AddTrustedRoot(root);
        Assert.False(store.IsTrusted(Path.Combine(_tempDir, "projects-evil", "malicious.sln")));
        Assert.True(store.IsTrusted(Path.Combine(_tempDir, "projects", "repo", "foo.sln"))); // genuine child
    }

    [Fact]
    public void AddPersistentTrust_IsIdempotent()
    {
        var store = new TrustStore(_trustFile);
        var path = Sln("repos", "foo.sln");
        store.AddPersistentTrust(path);
        store.AddPersistentTrust(path);

        var snapshot = store.GetSnapshot();
        Assert.Single(snapshot.PersistentSolutions);
    }

    [Fact]
    public void LoadFromDisk_CorruptFile_ReturnsEmptyAndLogsToStderr()
    {
        File.WriteAllText(_trustFile, "{ not valid json");
        var prevErr = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var store = new TrustStore(_trustFile);
            Assert.False(store.IsTrusted(Sln("anything")));
            Assert.Contains("trust.json", captured.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(prevErr);
        }
    }
}
