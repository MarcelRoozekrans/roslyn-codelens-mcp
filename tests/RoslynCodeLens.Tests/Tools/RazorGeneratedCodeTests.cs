using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// Regression guard for issue #399, on a Razor class library that builds with zero errors.
///
/// The whole class of bug was invisible because the Razor source generator silently did not run:
/// Roslyn refuses an analyzer built against a newer compiler than the host, returns zero
/// generators, and reports nothing. Everything downstream then answered confidently and wrongly.
/// These tests fail if generator output stops reaching the workspace, whatever the reason.
/// </summary>
[Collection("RazorSolution")]
public class RazorGeneratedCodeTests
{
    private readonly RazorSolutionFixture _fixture;

    public RazorGeneratedCodeTests(RazorSolutionFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The headline symptom. Counter.razor.cs overrides OnInitialized, which only compiles
    /// against the ComponentBase-derived half that the generator emits. Without the generator
    /// this reports CS0115/CS0117 on a project that `dotnet build` compiles cleanly — and an
    /// agent acting on that "fixes" code that was never broken.
    /// </summary>
    [Fact]
    public void GetDiagnostics_ReportsNoErrors_OnACleanRazorProject()
    {
        var errors = GetDiagnosticsLogic.Execute(_fixture.Loaded, _fixture.Resolver, project: null, severity: "error");

        Assert.Empty(errors);
    }

    /// <summary>NavMenu has no code-behind, so it exists only as generator output.</summary>
    [Fact]
    public void SearchSymbols_FindsAComponentThatHasNoCodeBehind()
    {
        var results = SearchSymbolsLogic.Execute(_fixture.Resolver, _fixture.Metadata, "NavMenu");

        var navMenu = Assert.Single(results, r => r.FullName.Contains("NavMenu", StringComparison.Ordinal));
        Assert.Equal("RazorLib", navMenu.Project);
        Assert.True(navMenu.IsGenerated, "a symbol declared in generator output must report isGenerated");
    }

    /// <summary>
    /// MainLayout.razor uses &lt;NavMenu /&gt; in markup and nowhere else. This returned zero
    /// references before the fix, which is the answer most likely to mislead: "nothing uses this,
    /// safe to delete".
    /// </summary>
    [Fact]
    public void FindReferences_FindsAComponentUsedOnlyFromMarkup()
    {
        var references = FindReferencesLogic.Execute(
            _fixture.Loaded, _fixture.Resolver, _fixture.Metadata, "RazorLib.Components.NavMenu");

        Assert.NotEmpty(references);
        Assert.Contains(references, r => r.File.Contains("MainLayout", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>.razor is an AdditionalDocument, so this used to be a flat FileNotFound.</summary>
    [Fact]
    public async Task GetFileOverview_ResolvesMarkupToItsGeneratedDocument()
    {
        var overview = await GetFileOverviewLogic.ExecuteAsync(
            _fixture.Loaded, _fixture.Resolver,
            _fixture.PathTo("Components", "Counter.razor"), CancellationToken.None);

        Assert.Equal("RazorLib", overview.Project);
        Assert.Contains("Counter", overview.TypesDefined);
    }

    [Fact]
    public async Task GetSourceGenerators_NamesTheRazorGenerator_AndCountsItsOutput()
    {
        var generators = await GetSourceGeneratorsLogic.ExecuteAsync(_fixture.Loaded, project: null);

        var razor = Assert.Single(generators, g =>
            g.GeneratorName.Equals(
                "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator", StringComparison.Ordinal));

        Assert.Equal("RazorLib", razor.Project);
        // One .g.cs per .razor file: _Imports, NavMenu, MainLayout, Counter.
        Assert.Equal(4, razor.GeneratedFileCount);
        Assert.All(razor.GeneratedFiles, f => Assert.EndsWith(".g.cs", f, StringComparison.Ordinal));
    }

    /// <summary>
    /// MSBuild-authored intermediates (AssemblyInfo.cs, GlobalUsings.g.cs, .AssemblyAttributes.cs)
    /// live in obj/ and were previously reported as generator output by a path heuristic.
    /// </summary>
    [Fact]
    public async Task GetSourceGenerators_ExcludesMsBuildIntermediates()
    {
        var generators = await GetSourceGeneratorsLogic.ExecuteAsync(_fixture.Loaded, project: null);

        var allFiles = generators.SelectMany(g => g.GeneratedFiles).ToList();

        Assert.DoesNotContain(allFiles, f => f.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allFiles, f => f.EndsWith("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(generators, g =>
            g.GeneratorName.Equals(GetSourceGeneratorsLogic.UnknownGenerator, StringComparison.Ordinal));
    }
}
