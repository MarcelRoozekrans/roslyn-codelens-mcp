using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Loads a solution whose projects see framework types in different assemblies: Net48Lib (net48)
/// sees System.IDisposable in mscorlib, NetStdLib (netstandard2.0) in netstandard and Net10App
/// (net10.0) in System.Runtime. The three projects are independent and contain the same code, so
/// every query for a framework symbol must report all three of them.
///
/// Deliberately a SEPARATE solution: the other fixtures target modern .NET only, and a .NET
/// Framework project there would churn their solution-wide assertions.
/// </summary>
public class ForwardedTypesSolutionFixture : IAsyncLifetime
{
    // Same design-time-build flake as TestSolutionFixture guards against (#260): a load can drop a
    // project's references, which would look exactly like the misses the tests assert on.
    private const int MaxLoadAttempts = 4;

    private static readonly string[] ProjectDirs = ["Net48Lib", "NetStdLib", "Net10App"];

    public string SolutionPath { get; private set; } = null!;
    public LoadedSolution Loaded { get; private set; } = null!;
    public SymbolResolver Resolver { get; private set; } = null!;
    public MetadataSymbolResolver Metadata { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        SolutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "ForwardedTypesSolution", "ForwardedTypesSolution.slnx"));

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
            $"ForwardedTypesSolution failed to load healthily after {MaxLoadAttempts} attempts: {problem}.");
    }

    /// <summary>
    /// Every project must be compiled and free of compiler errors; a dropped reference would
    /// otherwise look like the cross-framework misses the tests guard against.
    /// </summary>
    private static bool LoadIsHealthy(LoadedSolution loaded, out string problem)
    {
        if (loaded.Compilations.Count != ProjectDirs.Length)
        {
            problem = $"expected {ProjectDirs.Length} compiled projects, got {loaded.Compilations.Count}";
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
            "forwarded-types fixture",
            () => ProjectDirs.All(dir => File.Exists(Path.Combine(solutionDir, dir, "obj", "project.assets.json"))));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// xUnit collection definition that binds ForwardedTypesSolutionFixture to the
/// "ForwardedTypesSolution" collection name.
/// </summary>
[CollectionDefinition("ForwardedTypesSolution")]
public class ForwardedTypesSolutionCollection : ICollectionFixture<ForwardedTypesSolutionFixture> { }
