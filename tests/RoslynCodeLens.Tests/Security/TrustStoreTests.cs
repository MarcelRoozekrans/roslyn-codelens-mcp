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
    }

    [Fact]
    public void IsTrusted_EmptyStore_ReturnsFalse()
    {
        var store = new TrustStore(_trustFile);
        Assert.False(store.IsTrusted("c:\\repos\\foo.sln"));
    }

    [Fact]
    public void IsTrusted_AfterAddSession_ReturnsTrue_ButFileNotCreated()
    {
        var store = new TrustStore(_trustFile);
        store.AddSessionTrust("c:\\repos\\foo.sln");

        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));
        Assert.False(File.Exists(_trustFile));
    }

    [Fact]
    public void IsTrusted_AfterAddPersistent_ReturnsTrue_AndFileWritten()
    {
        var store = new TrustStore(_trustFile);
        store.AddPersistentTrust("c:\\repos\\foo.sln");

        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));
        Assert.True(File.Exists(_trustFile));

        var reloaded = new TrustStore(_trustFile);
        Assert.True(reloaded.IsTrusted("c:\\repos\\foo.sln"));
    }

    [Fact]
    public void IsTrusted_PathUnderTrustedRoot_ReturnsTrue()
    {
        var store = new TrustStore(_trustFile);
        store.AddTrustedRoot("c:\\projects\\");

        Assert.True(store.IsTrusted("c:\\projects\\repo\\foo.sln"));
        Assert.True(store.IsTrusted("c:\\projects\\nested\\dir\\bar.sln"));
        Assert.False(store.IsTrusted("c:\\other\\foo.sln"));
    }

    [Fact]
    public void Revoke_RemovesPersistentEntry()
    {
        var store = new TrustStore(_trustFile);
        store.AddPersistentTrust("c:\\repos\\foo.sln");
        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));

        store.Revoke("c:\\repos\\foo.sln");
        Assert.False(store.IsTrusted("c:\\repos\\foo.sln"));
    }

    [Fact]
    public void Revoke_RemovesSessionEntry()
    {
        var store = new TrustStore(_trustFile);
        store.AddSessionTrust("c:\\repos\\foo.sln");
        store.Revoke("c:\\repos\\foo.sln");
        Assert.False(store.IsTrusted("c:\\repos\\foo.sln"));
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
        store.AddSessionTrust("c:\\repos\\a.sln");
        store.AddPersistentTrust("c:\\repos\\b.sln");
        store.AddTrustedRoot("c:\\projects\\");

        var snapshot = store.GetSnapshot();
        Assert.Contains(snapshot.SessionSolutions, s => s.Equals("c:\\repos\\a.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.PersistentSolutions, s => s.Path.Equals("c:\\repos\\b.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.TrustedRoots, r => r.Equals("c:\\projects\\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TrustedRoot_DoesNotMatch_SiblingPrefixDirectory()
    {
        // Regression test for prefix-bypass: trusting "c:\projects" must NOT trust "c:\projects-evil\..."
        var store = new TrustStore(_trustFile);
        store.AddTrustedRoot("c:\\projects");  // no trailing slash — natural user input
        Assert.False(store.IsTrusted("c:\\projects-evil\\malicious.sln"));
        Assert.True(store.IsTrusted("c:\\projects\\repo\\foo.sln"));  // genuine child still works
    }

    [Fact]
    public void AddPersistentTrust_IsIdempotent()
    {
        var store = new TrustStore(_trustFile);
        store.AddPersistentTrust("c:\\repos\\foo.sln");
        store.AddPersistentTrust("c:\\repos\\foo.sln");

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
            Assert.False(store.IsTrusted("c:\\anything"));
            Assert.Contains("trust.json", captured.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetError(prevErr);
        }
    }
}
