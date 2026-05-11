using RoslynCodeLens.Security;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class RevokeTrustToolTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RemovesTrust()
    {
        var store = new TrustStore(_tempFile);
        store.AddPersistentTrust("c:\\repos\\foo.sln");
        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));

        RevokeTrustTool.Execute(store, "c:\\repos\\foo.sln");
        Assert.False(store.IsTrusted("c:\\repos\\foo.sln"));
    }
}
