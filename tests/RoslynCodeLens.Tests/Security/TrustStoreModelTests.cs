using System.Text.Json;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tests.Security;

public class TrustStoreModelTests
{
    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var model = new TrustStoreModel
        {
            Version = 1,
            TrustedRoots = ["c:\\projects\\", "c:\\work\\"],
            TrustedSolutions =
            [
                new TrustedSolution("c:\\repos\\foo.sln", DateTimeOffset.Parse("2026-05-11T10:00:00Z"))
            ],
            AnalyzerPolicy = "nuget-and-solution-bin"
        };

        var json = JsonSerializer.Serialize(model, TrustStoreModel.JsonOptions);
        var roundtripped = JsonSerializer.Deserialize<TrustStoreModel>(json, TrustStoreModel.JsonOptions);

        Assert.NotNull(roundtripped);
        Assert.Equal(1, roundtripped.Version);
        Assert.Equal(["c:\\projects\\", "c:\\work\\"], roundtripped.TrustedRoots);
        Assert.Single(roundtripped.TrustedSolutions);
        Assert.Equal("c:\\repos\\foo.sln", roundtripped.TrustedSolutions[0].Path);
        Assert.Equal("nuget-and-solution-bin", roundtripped.AnalyzerPolicy);
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var model = new TrustStoreModel();
        Assert.Equal(1, model.Version);
        Assert.Empty(model.TrustedRoots);
        Assert.Empty(model.TrustedSolutions);
        Assert.Equal("nuget-and-solution-bin", model.AnalyzerPolicy);
    }
}
