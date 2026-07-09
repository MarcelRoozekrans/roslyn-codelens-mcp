using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class LoaderConcurrencyHardeningTests
{
    private static string FixturePath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

    [Fact]
    public async Task CleanLoad_IsNotDegraded_AndHasNoLoadDiagnostics()
    {
        // Guards the false-positive risk: a healthy load emits a benign
        // WorkspaceDiagnosticKind.Failure (NU1510 "will not be pruned"), so degradation
        // must be judged by resolved references — not by diagnostics — or every load
        // would be reported degraded.
        var loaded = await new SolutionLoader().LoadAsync(FixturePath());

        Assert.False(loaded.Degraded);
        Assert.Empty(loaded.LoadDiagnostics);
    }

    [Fact]
    public async Task OpenAsync_CleanLoad_ReportsNoDroppedReferences()
    {
        var open = await new SolutionLoader().OpenAsync(FixturePath());

        Assert.Empty(open.LoadDiagnostics);
        // Every source-bearing project resolved its framework references.
        foreach (var project in open.Solution.Projects)
            if (project.DocumentIds.Count > 0)
                Assert.NotEmpty(project.MetadataReferences);
    }

    [Fact]
    public void Degraded_ReflectsLoadDiagnostics()
    {
        var healthy = new LoadedSolution
        {
            Solution = new AdhocWorkspace().CurrentSolution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(),
        };
        Assert.False(healthy.Degraded);

        var degraded = new LoadedSolution
        {
            Solution = new AdhocWorkspace().CurrentSolution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(),
            LoadDiagnostics = new[] { "Foo: no metadata references resolved" },
        };
        Assert.True(degraded.Degraded);
    }

    [Fact]
    public void MaxConcurrentBuildHosts_DefaultsToLoadParallelism()
    {
        Assert.Equal(SolutionLoader.GetLoadParallelism(), SolutionLoader.GetMaxConcurrentBuildHosts());
    }

    [Fact]
    public void MaxConcurrentBuildHosts_HonoursEnvOverride()
    {
        const string key = "ROSLYN_CODELENS_MAX_CONCURRENT_LOADS";
        var original = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "1");
            Assert.Equal(1, SolutionLoader.GetMaxConcurrentBuildHosts());
            Environment.SetEnvironmentVariable(key, "0"); // invalid -> falls back to default
            Assert.Equal(SolutionLoader.GetLoadParallelism(), SolutionLoader.GetMaxConcurrentBuildHosts());
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }

    [Fact]
    public void LoadRetryCount_DefaultsToTwo_AndHonoursEnvOverride()
    {
        Assert.Equal(2, SolutionLoader.GetLoadRetryCount());

        const string key = "ROSLYN_CODELENS_LOAD_RETRIES";
        var original = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "0"); // 0 is valid: disables retry
            Assert.Equal(0, SolutionLoader.GetLoadRetryCount());
            Environment.SetEnvironmentVariable(key, "5");
            Assert.Equal(5, SolutionLoader.GetLoadRetryCount());
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, original);
        }
    }
}
