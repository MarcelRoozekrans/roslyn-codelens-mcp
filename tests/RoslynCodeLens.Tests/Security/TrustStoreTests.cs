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

    // ---------------------------------------------------------------- transient file locks
    //
    // TrustStoreTests.AddPersistentTrust_IsIdempotent failed intermittently on Windows with
    // UnauthorizedAccessException out of File.Move in SaveToDisk. Each test writes to its own
    // GUID temp directory, so it was never cross-test collision — it is a scanner or indexer
    // momentarily holding the file between the write and the rename.
    //
    // The retry is tested through an injected operation rather than by trying to provoke a real
    // lock: a timing-dependent test for a timing-dependent bug proves nothing on a green run.

    [Fact]
    public void RunWithRetry_RetriesTransientSharingViolations_ThenSucceeds()
    {
        var attempts = 0;
        TrustStore.RunWithRetry(() =>
        {
            attempts++;
            if (attempts < 3) throw new UnauthorizedAccessException("Access to the path is denied.");
        }, maxAttempts: 5, baseDelayMs: 1);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void RunWithRetry_RetriesIOException()
    {
        var attempts = 0;
        TrustStore.RunWithRetry(() =>
        {
            attempts++;
            if (attempts < 2) throw new IOException("The process cannot access the file.");
        }, maxAttempts: 5, baseDelayMs: 1);

        Assert.Equal(2, attempts);
    }

    /// <summary>
    /// A persistent failure must surface as itself. Swallowing it would turn "this file is
    /// read-only" into silent data loss — the caller believes trust was persisted when it was not.
    /// </summary>
    [Fact]
    public void RunWithRetry_GivesUpAndRethrowsTheOriginalException()
    {
        var attempts = 0;
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            TrustStore.RunWithRetry(() =>
            {
                attempts++;
                throw new UnauthorizedAccessException("permanently denied");
            }, maxAttempts: 4, baseDelayMs: 1));

        Assert.Equal(4, attempts);
        Assert.Equal("permanently denied", ex.Message);
    }

    /// <summary>
    /// Unrelated exceptions must not be retried — retrying a bug wastes time and hides it.
    /// </summary>
    [Fact]
    public void RunWithRetry_DoesNotRetryUnrelatedExceptions()
    {
        var attempts = 0;
        Assert.Throws<InvalidOperationException>(() =>
            TrustStore.RunWithRetry(() =>
            {
                attempts++;
                throw new InvalidOperationException("not a file problem");
            }, maxAttempts: 5, baseDelayMs: 1));

        Assert.Equal(1, attempts);
    }

    /// <summary>
    /// The reason the temp name carries a GUID: back-to-back saves must not reuse the path just
    /// renamed away, which is the window the flake lived in.
    /// </summary>
    [Fact]
    public void RepeatedSaves_LeaveNoTempFilesBehind()
    {
        var store = new TrustStore(_trustFile);
        for (var i = 0; i < 10; i++)
            store.AddPersistentTrust(Sln("repos", $"foo{i}.sln"));

        Assert.True(File.Exists(_trustFile));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
        Assert.Equal(10, store.GetSnapshot().PersistentSolutions.Count);
    }
}
