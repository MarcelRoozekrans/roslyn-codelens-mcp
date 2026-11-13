using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// Issue #549, end to end on a real generator whose assembly and type names both contain dots:
/// the regex generator that ships with the SDK, exercised by this repository's own
/// [GeneratedRegex] usage. Unlike the Razor fixture this does not depend on the host's Roslyn
/// being new enough to run the Razor compiler.
/// </summary>
public class GeneratorNamingAgreementTests
{
    private const string RegexGenerator = "System.Text.RegularExpressions.Generator.RegexGenerator";

    [Fact]
    public async Task GetGeneratedCode_AndGetSourceGenerators_AgreeOnTheGeneratorName()
    {
        var projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "RoslynCodeLens", "RoslynCodeLens.csproj"));
        var tempDir = Directory.CreateTempSubdirectory("codelens-549-");
        try
        {
            var slnx = Path.Combine(tempDir.FullName, "Repro.slnx");
            await File.WriteAllTextAsync(slnx, $"<Solution><Project Path=\"{projectPath}\" /></Solution>");
            await AssertAgreementAsync(await new SolutionLoader().LoadAsync(slnx));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static async Task AssertAgreementAsync(LoadedSolution loaded)
    {
        var resolver = new SymbolResolver(loaded);

        var generators = await GetSourceGeneratorsLogic.ExecuteAsync(loaded, project: null);
        Assert.Contains(generators, g => string.Equals(g.GeneratorName, RegexGenerator, StringComparison.Ordinal));

        var all = GetGeneratedCodeLogic.Execute(loaded, resolver, generator: null, file: "RegexGenerator");
        // The project multi-targets, so one file per target framework.
        Assert.NotEmpty(all);
        Assert.All(all, f => Assert.Equal(RegexGenerator, f.GeneratorName));

        var filtered = GetGeneratedCodeLogic.Execute(loaded, resolver, RegexGenerator, file: null);
        Assert.Equal(all.Count, filtered.Count);
    }
}
