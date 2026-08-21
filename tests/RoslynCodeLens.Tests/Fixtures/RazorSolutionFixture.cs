using System.Diagnostics;
using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Loads a small Razor class library once per assembly, for the generated-code tests
/// (<see cref="Tests.Tools.RazorGeneratedCodeTests"/>).
///
/// Deliberately a SEPARATE solution rather than another project in TestSolution: the shared
/// fixture is asserted against by solution-wide tests (project counts, symbol sets, coverage),
/// and adding a Razor project there would churn them for no benefit. The cost is one extra
/// single-project solution load.
/// </summary>
public class RazorSolutionFixture : IAsyncLifetime
{
    // Same design-time-build flake as TestSolutionFixture guards against (#260): a load can drop
    // a project's references. This fixture is one project so it is far less exposed, but the
    // failure mode — an empty/degraded compilation — would surface as a confusing generator
    // assertion failure rather than an obvious load problem, so retry on a fresh workspace.
    private const int MaxLoadAttempts = 4;

    public string SolutionPath { get; private set; } = null!;
    public LoadedSolution Loaded { get; private set; } = null!;
    public SymbolResolver Resolver { get; private set; } = null!;
    public MetadataSymbolResolver Metadata { get; private set; } = null!;

    /// <summary>Absolute path to a file inside the fixture project.</summary>
    public string PathTo(params string[] parts) =>
        Path.GetFullPath(Path.Combine(
            new[] { Path.GetDirectoryName(SolutionPath)!, "RazorLib" }.Concat(parts).ToArray()));

    public async Task InitializeAsync()
    {
        SolutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "RazorFixture", "RazorFixture.slnx"));

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
            $"RazorFixture failed to load healthily after {MaxLoadAttempts} attempts: {problem}.");
    }

    /// <summary>
    /// Checks only that the project loaded with its references intact — deliberately NOT that
    /// generators ran. Generator output is what the tests assert on; probing for it here would
    /// turn a real regression into an opaque "fixture failed to load" error.
    /// </summary>
    private static bool LoadIsHealthy(LoadedSolution loaded, out string problem)
    {
        if (loaded.Compilations.Count != 1)
        {
            problem = $"expected 1 compiled project, got {loaded.Compilations.Count}";
            return false;
        }

        var project = loaded.Solution.Projects.First();
        if (project.MetadataReferences.Count == 0)
        {
            problem = $"{project.Name} loaded with no metadata references (design-time build dropped them)";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private async Task EnsureRestoredAsync()
    {
        var assets = Path.Combine(
            Path.GetDirectoryName(SolutionPath)!, "RazorLib", "obj", "project.assets.json");
        if (File.Exists(assets))
            return;

        var psi = new ProcessStartInfo("dotnet", $"restore \"{SolutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'dotnet restore' for the Razor fixture.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet restore' for the Razor fixture failed (exit {process.ExitCode}):\n" +
                $"{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("RazorSolution")]
public class RazorSolutionCollection : ICollectionFixture<RazorSolutionFixture> { }
