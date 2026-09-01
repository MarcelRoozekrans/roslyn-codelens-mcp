using System.Diagnostics;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Loads a two-project solution whose test project gets its xunit reference exclusively from
/// a shared <c>tests/Directory.Build.props</c> — the csproj itself contains no test-framework
/// <c>PackageReference</c>. Guards #406: test-project detection must follow MSBuild evaluation,
/// not csproj text.
///
/// Deliberately a SEPARATE solution rather than another project in TestSolution: the shared
/// fixture is asserted against by solution-wide tests (project counts, symbol sets, coverage),
/// and adding a project there would churn them for no benefit. The cost is one extra
/// two-project solution load.
/// </summary>
public class PropsSolutionFixture : IAsyncLifetime
{
    // Same design-time-build flake as TestSolutionFixture guards against (#260): a load can
    // drop a project's references. Here that failure mode would look exactly like the #406
    // regression this fixture exists to catch, so the health probe below distinguishes them
    // and we retry on a fresh workspace.
    private const int MaxLoadAttempts = 4;

    public string SolutionPath { get; private set; } = null!;
    public LoadedSolution Loaded { get; private set; } = null!;
    public SymbolResolver Resolver { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        SolutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "PropsFixture", "PropsFixture.slnx"));

        await EnsureRestoredAsync().ConfigureAwait(false);

        var problem = string.Empty;
        for (var attempt = 1; attempt <= MaxLoadAttempts; attempt++)
        {
            Loaded = await new SolutionLoader().LoadAsync(SolutionPath).ConfigureAwait(false);
            if (LoadIsHealthy(Loaded, out problem))
            {
                Resolver = new SymbolResolver(Loaded);
                return;
            }
        }

        throw new InvalidOperationException(
            $"PropsFixture failed to load healthily after {MaxLoadAttempts} attempts: {problem}.");
    }

    /// <summary>
    /// Checks that the design-time build actually resolved the props-declared xunit reference —
    /// <c>Xunit.FactAttribute</c> must bind in the test project's compilation. That separates a
    /// degraded load (references dropped, retry) from the detection regression the tests assert
    /// on: the detector never sees compilations, so a healthy load proves nothing about it.
    /// </summary>
    private static bool LoadIsHealthy(LoadedSolution loaded, out string problem)
    {
        if (loaded.Compilations.Count != 2)
        {
            problem = $"expected 2 compiled projects, got {loaded.Compilations.Count}";
            return false;
        }

        var testProject = loaded.Solution.Projects.FirstOrDefault(
            p => string.Equals(p.Name, "PropsDrivenTests", StringComparison.Ordinal));
        if (testProject is null)
        {
            problem = "PropsDrivenTests project missing from the loaded solution";
            return false;
        }

        var compilation = loaded.Compilations[testProject.Id];
        if (compilation.GetTypeByMetadataName("Xunit.FactAttribute") is null)
        {
            problem = "Xunit.FactAttribute does not resolve in PropsDrivenTests " +
                      "(design-time build dropped the Directory.Build.props-declared reference)";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private async Task EnsureRestoredAsync()
    {
        var assets = Path.Combine(
            Path.GetDirectoryName(SolutionPath)!, "tests", "PropsDrivenTests", "obj", "project.assets.json");
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
            ?? throw new InvalidOperationException("Failed to start 'dotnet restore' for the Props fixture.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'dotnet restore' for the Props fixture failed (exit {process.ExitCode}):\n" +
                $"{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}");
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("PropsSolution")]
public class PropsSolutionCollection : ICollectionFixture<PropsSolutionFixture> { }
