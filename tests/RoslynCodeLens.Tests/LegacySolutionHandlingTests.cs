using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Verifies graceful handling of legacy (non-SDK-style) .NET Framework projects.
/// Regression tests for https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/175
/// </summary>
public class LegacySolutionHandlingTests
{
    private static string LegacySolutionPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "LegacySolution", "Legacy.sln"));

    private static string ValidSolutionPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

    // The full-load path for legacy projects runs only on Windows with Visual Studio MSBuild; elsewhere the
    // test is reported as Skipped, not as a pass that checked nothing.
    private sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        public WindowsOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Loading legacy non-SDK-style projects needs Visual Studio MSBuild on Windows.";
        }
    }

    [WindowsOnlyFact]
    public async Task OpenAsync_MixedLegacyProjects_LoadsBuildableAndReportsSkippedOnWindows()
    {
        var solutionPath = Path.Combine(Path.GetDirectoryName(LegacySolutionPath)!, "LoadableLegacy.slnx");

        var (solution, workspace, skipped) = await new SolutionLoader().OpenAsync(solutionPath);
        using var _ = workspace;

        var project = Assert.Single(solution.Projects);
        Assert.Equal("LoadableLegacyLib", project.Name);
        Assert.Contains(skipped, entry => entry is { Name: "MissingProject", Kind: "Missing" });
        Assert.Contains(skipped, entry => entry is { Name: "LegacyLib", Kind: "Legacy" });
        Assert.Contains(skipped, entry => entry is { Name: "UnsupportedProject", Kind: "Unknown" });
        Assert.Equal(3, skipped.Count);

        var compilation = await project.GetCompilationAsync();

        Assert.NotNull(compilation);
        Assert.NotNull(compilation.GetTypeByMetadataName("LegacyLib.LegacyClass"));
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task SolutionManager_CreateAsync_DoesNotThrowOnLegacyProject()
    {
        var manager = await SolutionManager.CreateAsync(LegacySolutionPath);
        manager.Dispose();
    }

    [Fact]
    public async Task SolutionManager_LegacyProject_PopulatesSkippedList()
    {
        var manager = await SolutionManager.CreateAsync(LegacySolutionPath);
        await manager.WaitForWarmupAsync();

        var loaded = manager.GetLoadedSolution();
        Assert.Empty(loaded.Solution.Projects);
        Assert.NotEmpty(loaded.SkippedProjects);
        Assert.Contains(loaded.SkippedProjects, p => string.Equals(p.Kind, "Legacy", StringComparison.Ordinal));
        Assert.Contains(loaded.SkippedProjects, p => p.Reason.Contains("non-SDK", StringComparison.OrdinalIgnoreCase));

        manager.Dispose();
    }

    [Fact]
    public async Task MultiSolutionManager_LegacyOnly_LoadsWithSkippedReported()
    {
        var manager = await MultiSolutionManager.CreateAsync(new[] { LegacySolutionPath });

        var solutions = manager.ListSolutions();
        var solution = Assert.Single(solutions);
        Assert.NotEmpty(solution.SkippedProjects);
        Assert.Contains(solution.SkippedProjects, p => string.Equals(p.Kind, "Legacy", StringComparison.Ordinal));

        manager.Dispose();
    }

    [Fact]
    public async Task MultiSolutionManager_MixedSolutions_LoadsValidAndReportsLegacySkipped()
    {
        var manager = await MultiSolutionManager.CreateAsync(new[] { LegacySolutionPath, ValidSolutionPath });

        var solutions = manager.ListSolutions();
        Assert.Equal(2, solutions.Count);

        var legacy = solutions.Single(s => s.Path.EndsWith("Legacy.sln", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(legacy.SkippedProjects);

        var valid = solutions.Single(s => s.Path.EndsWith("TestSolution.slnx", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(valid.SkippedProjects);
        Assert.Equal("ready", valid.Status);

        manager.Dispose();
    }

    [Fact]
    public async Task LoadSolutionTool_LegacySolution_ReturnsSkippedSummary()
    {
        var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();

        var result = (string)await LoadSolutionTool.Execute(manager, store, LegacySolutionPath);

        Assert.Contains("Loaded", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Skipped", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Legacy", result, StringComparison.OrdinalIgnoreCase);

        manager.Dispose();
    }

    [Fact]
    public void ProjectClassifier_DetectsLegacyAndSdkStyle()
    {
        var classified = ProjectClassifier.EnumerateProjects(LegacySolutionPath);
        Assert.NotEmpty(classified);
        Assert.Contains(classified, c => c.Kind == ProjectClassifier.ProjectKind.Legacy);

        var validClassified = ProjectClassifier.EnumerateProjects(ValidSolutionPath);
        Assert.NotEmpty(validClassified);
        Assert.All(validClassified, c => Assert.Equal(ProjectClassifier.ProjectKind.SdkStyle, c.Kind));
    }
}
