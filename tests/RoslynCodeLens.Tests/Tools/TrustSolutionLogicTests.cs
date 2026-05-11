using RoslynCodeLens.Security;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class TrustSolutionLogicTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SessionScope_AddsSessionTrust_NoFileWritten()
    {
        var store = new TrustStore(_tempFile);
        var result = TrustSolutionLogic.Execute(store, "c:\\repos\\foo.sln", "session");
        Assert.Equal("session", result.Scope);
        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));
        Assert.False(File.Exists(_tempFile));
    }

    [Fact]
    public void PersistentScope_AddsPersistentTrust_FileWritten()
    {
        var store = new TrustStore(_tempFile);
        var result = TrustSolutionLogic.Execute(store, "c:\\repos\\foo.sln", "persistent");
        Assert.Equal("persistent", result.Scope);
        Assert.True(File.Exists(_tempFile));
    }

    [Fact]
    public void AddRootScope_AddsTrustedRoot()
    {
        var store = new TrustStore(_tempFile);
        var rootDir = Path.Combine(Path.GetTempPath(), $"trust-root-{Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
        var childSln = Path.Combine(rootDir, "anyrepo", "foo.sln");
        var result = TrustSolutionLogic.Execute(store, rootDir, "addRoot");
        Assert.Equal("addRoot", result.Scope);
        Assert.True(store.IsTrusted(childSln));
    }

    [Fact]
    public void InvalidScope_Throws()
    {
        var store = new TrustStore(_tempFile);
        Assert.Throws<ArgumentException>(() =>
            TrustSolutionLogic.Execute(store, "c:\\repos\\foo.sln", "lifetime"));
    }
}
