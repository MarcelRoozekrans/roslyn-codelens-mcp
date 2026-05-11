using RoslynCodeLens.Security;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class ListTrustedPathsToolTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Returns_AllTrustEntries()
    {
        var store = new TrustStore(_tempFile);
        store.AddSessionTrust("c:\\repos\\a.sln");
        store.AddPersistentTrust("c:\\repos\\b.sln");
        store.AddTrustedRoot("c:\\projects\\");

        var result = ListTrustedPathsTool.Execute(store);

        Assert.Contains(result.SessionSolutions, s => s.EndsWith("a.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.PersistentSolutions, s => s.Path.EndsWith("b.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.TrustedRoots, r => r.Contains("projects", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("nuget-and-solution-bin", result.AnalyzerPolicy);
    }
}
