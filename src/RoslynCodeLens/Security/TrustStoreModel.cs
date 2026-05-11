using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynCodeLens.Security;

public sealed class TrustStoreModel
{
    public int Version { get; set; } = 1;
    public List<string> TrustedRoots { get; set; } = [];
    public List<TrustedSolution> TrustedSolutions { get; set; } = [];
    public string AnalyzerPolicy { get; set; } = "nuget-and-solution-bin";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record TrustedSolution(string Path, DateTimeOffset AddedUtc);
