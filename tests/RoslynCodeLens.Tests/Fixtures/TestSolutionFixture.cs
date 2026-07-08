using System.Diagnostics;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.TestDiscovery;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Shared fixture that loads the test solution once per assembly. Used by tests via
/// [Collection("TestSolution")]. Loading once eliminates per-test MSBuildWorkspace
/// flakiness on Linux CI and dramatically speeds up the test suite.
/// </summary>
public class TestSolutionFixture : IAsyncLifetime
{
    // Loading the fixture solution through MSBuildWorkspace is nondeterministic on
    // this codebase: a design-time build occasionally drops a project's references,
    // after which [Fact]/[Test]/[TestMethod] bind to error types or the test body's
    // call into TestLib.Greeter no longer binds. Either way symbol-based discovery
    // and coverage over the fixture silently return empty/wrong results — the flake
    // behind #260 (and the long-standing "green in CI, red locally" fixture failures).
    // Rather than hand tests a degraded solution, we probe the real discovery path
    // after each load and retry on a fresh workspace until it is healthy. A load is an
    // independent draw, so retries converge quickly; if every attempt fails we throw a
    // precise diagnostic instead of letting tests fail with "Collection: []".
    private const int MaxLoadAttempts = 6;

    private static readonly TestFramework[] RequiredFrameworks =
        [TestFramework.XUnit, TestFramework.NUnit, TestFramework.MSTest];

    private static readonly string[] FixtureProjectDirs = ["XUnitFixture", "NUnitFixture", "MSTestFixture"];

    public string SolutionPath { get; private set; } = null!;
    public LoadedSolution Loaded { get; private set; } = null!;
    public SymbolResolver Resolver { get; private set; } = null!;
    public MetadataSymbolResolver Metadata { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        SolutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

        // Restore up front only when the assets are missing, so already-restored
        // runs (CI, warm local checkouts) pay nothing.
        await EnsureFixturesRestoredAsync(force: false).ConfigureAwait(false);

        var problem = string.Empty;
        for (var attempt = 1; attempt <= MaxLoadAttempts; attempt++)
        {
            Loaded = await new SolutionLoader().LoadAsync(SolutionPath).ConfigureAwait(false);
            var resolver = new SymbolResolver(Loaded);
            if (FixtureIsHealthy(Loaded, resolver, out problem))
            {
                Resolver = resolver;
                Metadata = new MetadataSymbolResolver(Loaded, Resolver);
                return;
            }
        }

        throw new InvalidOperationException(
            $"TestSolution failed to load healthily after {MaxLoadAttempts} attempts: {problem}. " +
            "This is the MSBuildWorkspace design-time build flake from #260 — a fixture project's references were " +
            "dropped, so [Fact]/[Test]/[TestMethod] or the call into TestLib.Greeter did not bind and discovery returns nothing.");
    }

    /// <summary>
    /// Probes the actual discovery and coverage paths — the same code the fixture's
    /// tests exercise — rather than a shallow metadata check, because a dropped
    /// reference can leave the framework/type resolvable via metadata while
    /// cross-project reference finding still comes back empty. Two invariants, both
    /// resting on the same test→TestLib.Greeter reference resolving:
    ///   * Greeter.Greet has a direct test caller in every framework fixture; and
    ///   * Greeter.Greet is not reported as uncovered.
    /// A load that fails either dropped references — the caller retries on a fresh
    /// workspace. Checking coverage as well as discovery matters: the two use slightly
    /// different reference queries, and a load can satisfy one while degrading the other.
    /// </summary>
    private static bool FixtureIsHealthy(LoadedSolution loaded, SymbolResolver resolver, out string problem)
    {
        var discovery = FindTestsForSymbolLogic.Execute(loaded, resolver, "Greeter.Greet", transitive: false, maxDepth: 3);
        var found = discovery.DirectTests.Select(t => t.Framework).ToHashSet();
        var missing = RequiredFrameworks.Where(f => !found.Contains(f)).ToList();
        if (missing.Count > 0)
        {
            problem = $"discovery for Greeter.Greet is missing framework(s) [{string.Join(", ", missing)}] " +
                      $"(found {discovery.DirectTests.Count} direct test caller(s))";
            return false;
        }

        var coverage = FindUncoveredSymbolsLogic.Execute(loaded, resolver);
        if (coverage.UncoveredSymbols.Any(s => string.Equals(s.Symbol, "Greeter.Greet", StringComparison.Ordinal)))
        {
            problem = "Greeter.Greet is reported uncovered despite having test callers (coverage reference query degraded)";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private async Task EnsureFixturesRestoredAsync(bool force)
    {
        if (!force && FixturesRestored())
            return;

        await RunDotnetRestoreAsync(SolutionPath).ConfigureAwait(false);
    }

    private bool FixturesRestored()
    {
        var solutionDir = Path.GetDirectoryName(SolutionPath)!;
        foreach (var dir in FixtureProjectDirs)
        {
            if (!File.Exists(Path.Combine(solutionDir, dir, "obj", "project.assets.json")))
                return false;
        }
        return true;
    }

    private static async Task RunDotnetRestoreAsync(string solutionPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'dotnet restore' for the test fixtures.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet restore' for the test fixtures failed (exit {process.ExitCode}):\n" +
                $"{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// xUnit collection definition that binds TestSolutionFixture to the "TestSolution"
/// collection name. Tests opt in via [Collection("TestSolution")].
/// </summary>
[CollectionDefinition("TestSolution")]
public class TestSolutionCollection : ICollectionFixture<TestSolutionFixture> { }
