using Microsoft.CodeAnalysis;
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Covers the parallel-pool + re-stitch loader (issue #232, deferred item a).
/// The existing <see cref="SolutionLoaderFilterTests"/> already prove that filtered
/// loads return the correct project *set* through this code path; these tests pin
/// the behaviour that is new and risky: the references survive being re-stitched out
/// of N isolated worker workspaces into one.
/// </summary>
public class ParallelLoaderTests
{
    private static string Slnx()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "FilterableSolution", "FilterableSolution.slnx");
        return Path.GetFullPath(path);
    }

    [Fact]
    public async Task OpenAsync_RestitchedSolution_PreservesProjectReferenceEdges()
    {
        var loader = new SolutionLoader();
        var filter = new ProjectFilter(Include: Array.Empty<string>(), RootProjects: new[] { "App.Api" });

        var (solution, workspace, _) = await loader.OpenAsync(Slnx(), filter);
        using var _ws = workspace;

        var api = solution.Projects.Single(p => p.Name == "App.Api");

        // Re-stitch must rebuild App.Api's two direct ProjectReferences as live
        // references that resolve to the in-solution App.Domain / App.Infrastructure
        // projects — not dangling ids from the worker workspace they were loaded in.
        var referencedNames = api.ProjectReferences
            .Select(r => solution.GetProject(r.ProjectId)?.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "App.Domain", "App.Infrastructure" }, referencedNames);
    }

    [Fact]
    public async Task OpenAsync_RestitchedSolution_CompilationResolvesAcrossProjectReference()
    {
        var loader = new SolutionLoader();
        var filter = new ProjectFilter(Include: Array.Empty<string>(), RootProjects: new[] { "App.Api" });

        var (solution, workspace, _) = await loader.OpenAsync(Slnx(), filter);
        using var _ws = workspace;

        var api = solution.Projects.Single(p => p.Name == "App.Api");
        var compilation = await api.GetCompilationAsync();
        Assert.NotNull(compilation);

        // If the re-stitched ProjectReference is wired correctly, the referenced
        // project's public type is visible to App.Api's compilation.
        var domainType = compilation!.GetTypeByMetadataName("App.Domain.Class1");
        Assert.NotNull(domainType);

        // And the compilation should have no unresolved-reference errors.
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public async Task OpenAsync_SingleWorker_MatchesParallelResult()
    {
        var loader = new SolutionLoader();
        var filter = new ProjectFilter(Include: new[] { "App.*" }, RootProjects: Array.Empty<string>());

        var parallelNames = await LoadNamesAsync(loader, filter, parallelism: null);
        var singleNames = await LoadNamesAsync(loader, filter, parallelism: "1");

        // Degree of parallelism is a performance knob only — it must never change
        // which projects end up loaded.
        Assert.Equal(parallelNames, singleNames);
        Assert.Equal(
            new[] { "App.Api", "App.Api.Tests", "App.Domain", "App.Infrastructure", "Shared.Common" },
            singleNames);
    }

    private static async Task<string[]> LoadNamesAsync(SolutionLoader loader, ProjectFilter filter, string? parallelism)
    {
        const string Key = "ROSLYN_CODELENS_LOAD_PARALLELISM";
        var previous = Environment.GetEnvironmentVariable(Key);
        Environment.SetEnvironmentVariable(Key, parallelism);
        try
        {
            var (solution, workspace, _) = await loader.OpenAsync(Slnx(), filter);
            using var _ws = workspace;
            return solution.Projects.Select(p => p.Name).OrderBy(n => n).ToArray();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Key, previous);
        }
    }

    [Fact]
    public void GetLoadParallelism_HonoursEnvironmentOverride()
    {
        const string Key = "ROSLYN_CODELENS_LOAD_PARALLELISM";
        var previous = Environment.GetEnvironmentVariable(Key);
        try
        {
            Environment.SetEnvironmentVariable(Key, "3");
            Assert.Equal(3, SolutionLoader.GetLoadParallelism());

            Environment.SetEnvironmentVariable(Key, "0");      // invalid -> default
            Assert.True(SolutionLoader.GetLoadParallelism() >= 1);

            Environment.SetEnvironmentVariable(Key, "notanumber");
            Assert.True(SolutionLoader.GetLoadParallelism() >= 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Key, previous);
        }
    }
}
