using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Loads a solution in which the same source type exists as several symbol instances: the
/// multi-targeted <c>Shared</c> project compiles once per target framework, and the net9.0 and
/// net10.0 consumers each bind to a different instance of every <c>Shared</c> type.
///
/// Deliberately a SEPARATE solution rather than more projects in TestSolution: TestSolution is
/// single-targeted on purpose and asserted against by solution-wide tests, and a multi-targeted
/// project there would churn them.
/// </summary>
public class MultiInstanceSolutionFixture : IAsyncLifetime
{
    // Same design-time-build flake as TestSolutionFixture guards against (#260): a load can drop a
    // project's references. Here that would look like the cross-instance misses the tests assert
    // on, so the health probe below tells them apart and we retry on a fresh workspace.
    private const int MaxLoadAttempts = 4;

    // Shared compiles once per target framework, plus two app and two test projects.
    private const int ExpectedCompilations = 6;

    private static readonly string[] ProjectDirs = ["Shared", "AppNet9", "AppNet10", "TestsNet9", "TestsNet10"];

    public string SolutionPath { get; private set; } = null!;
    public LoadedSolution Loaded { get; private set; } = null!;
    public SymbolResolver Resolver { get; private set; } = null!;
    public MetadataSymbolResolver Metadata { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        SolutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "MultiInstanceSolution", "MultiInstanceSolution.slnx"));

        await EnsureRestoredAsync().ConfigureAwait(false);

        var problem = string.Empty;
        for (var attempt = 1; attempt <= MaxLoadAttempts; attempt++)
        {
            Loaded = await new SolutionLoader().LoadAsync(SolutionPath).ConfigureAwait(false);
            if (LoadIsHealthy(Loaded, out problem))
            {
                Resolver = new SymbolResolver(Loaded);
                Metadata = new MetadataSymbolResolver(Loaded, Resolver);
                return;
            }
        }

        throw new InvalidOperationException(
            $"MultiInstanceSolution failed to load healthily after {MaxLoadAttempts} attempts: {problem}.");
    }

    /// <summary>
    /// Every project must be compiled and free of compiler errors. A load that dropped a reference
    /// leaves unresolved names (CS0246 and friends) behind, which would otherwise look exactly like
    /// the cross-instance misses the tests guard against.
    /// </summary>
    private static bool LoadIsHealthy(LoadedSolution loaded, out string problem)
    {
        if (loaded.Compilations.Count != ExpectedCompilations)
        {
            problem = $"expected {ExpectedCompilations} compiled projects, got {loaded.Compilations.Count}";
            return false;
        }

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var error = compilation.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            if (error is not null)
            {
                problem = $"{loaded.Solution.GetProject(projectId)?.Name} has compiler errors, first: {error}";
                return false;
            }
        }

        problem = string.Empty;
        return true;
    }

    private Task EnsureRestoredAsync()
    {
        var solutionDir = Path.GetDirectoryName(SolutionPath)!;
        return FixtureRestore.EnsureAsync(
            SolutionPath,
            "multi-instance fixture",
            () => ProjectDirs.All(dir => File.Exists(Path.Combine(solutionDir, dir, "obj", "project.assets.json"))));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// xUnit collection definition that binds MultiInstanceSolutionFixture to the
/// "MultiInstanceSolution" collection name.
/// </summary>
[CollectionDefinition("MultiInstanceSolution")]
public class MultiInstanceSolutionCollection : ICollectionFixture<MultiInstanceSolutionFixture> { }
