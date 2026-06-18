using BenchmarkDotNet.Attributes;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using RoslynCodeLens;

namespace RoslynCodeLens.Benchmarks;

/// <summary>
/// Compares sequential (single-worker) vs parallel project loading for the
/// per-project loader path (issue #232, deferred item a). The bundled fixture is
/// small, so the absolute numbers are modest — the value is the head-to-head ratio
/// and a ready harness to point at a large real solution by overriding
/// <c>ROSLYN_CODELENS_BENCH_SOLUTION</c>.
/// </summary>
[MemoryDiagnoser]
public class SolutionLoadBenchmarks
{
    private const string ParallelismKey = "ROSLYN_CODELENS_LOAD_PARALLELISM";
    private static readonly string SolutionPath;

    static SolutionLoadBenchmarks()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
        SolutionPath = ResolveSolutionPath();
    }

    private static string ResolveSolutionPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("ROSLYN_CODELENS_BENCH_SOLUTION");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RoslynCodeLens.slnx")))
            dir = dir.Parent;

        return dir == null
            ? throw new InvalidOperationException(
                "Could not find repo root (RoslynCodeLens.slnx) starting from " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName,
                "tests", "RoslynCodeLens.Tests", "Fixtures", "FilterableSolution", "FilterableSolution.slnx");
    }

    // Force the per-project loader (rather than the monolithic OpenSolutionAsync)
    // by seeding an all-projects filter; that is the path the parallelisation
    // changed and the one a large solution would hit after filtering.
    private static ProjectFilter AllProjectsFilter => new(Include: new[] { "*" }, RootProjects: Array.Empty<string>());

    [Benchmark(Baseline = true, Description = "OpenAsync (sequential, parallelism=1)")]
    public async Task<int> LoadSequential()
    {
        Environment.SetEnvironmentVariable(ParallelismKey, "1");
        return await OpenAndCountAsync().ConfigureAwait(false);
    }

    [Benchmark(Description = "OpenAsync (parallel, default parallelism)")]
    public async Task<int> LoadParallel()
    {
        Environment.SetEnvironmentVariable(ParallelismKey, null);
        return await OpenAndCountAsync().ConfigureAwait(false);
    }

    private static async Task<int> OpenAndCountAsync()
    {
        var (solution, workspace, _) = await new SolutionLoader()
            .OpenAsync(SolutionPath, AllProjectsFilter).ConfigureAwait(false);
        using (workspace as IDisposable)
        {
            return solution.Projects.Count();
        }
    }
}
