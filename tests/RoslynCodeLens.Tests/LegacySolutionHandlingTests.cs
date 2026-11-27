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
    public void LegacyLoadFailureReason_NoFailureNamesTheProject_GivesTheGenericReason()
    {
        var reason = SolutionLoader.LegacyLoadFailureReason(
            @"C:\src\LegacyLib\LegacyLib.csproj", ["Msbuild failed when processing the file 'C:\\src\\Other\\Other.csproj'."]);

        Assert.Equal("MSBuild could not load the legacy non-SDK-style project.", reason);
    }

    [Fact]
    public void LegacyLoadFailureReason_FailureNamesTheProject_IncludesTheCause()
    {
        const string cause = "Msbuild failed when processing the file 'C:\\SRC\\LegacyLib\\LegacyLib.csproj': MSB3644 targeting pack not found.";

        var reason = SolutionLoader.LegacyLoadFailureReason(
            @"C:\src\LegacyLib\LegacyLib.csproj", ["unrelated warning", cause]);

        Assert.Equal("MSBuild could not load the legacy non-SDK-style project: " + cause, reason);
    }

    [Fact]
    public void RemoveUnloadedProjects_DropsEmptyPlaceholdersAndKeepsLoadedProjects()
    {
        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        solution = AddProject(solution, "Loaded", "/src/Loaded/Loaded.csproj", withDocument: true, withFramework: true);
        solution = AddProject(solution, "Legacy", "/src/Legacy/Legacy.csproj", withDocument: false);
        solution = AddProject(solution, "Odd", "/src/Odd/Odd.sqlproj", withDocument: false);

        ProjectClassifier.ClassifiedProject[] toCheck =
        [
            new("/src/Loaded/Loaded.csproj", "Loaded", ProjectClassifier.ProjectKind.Legacy, "classifier reason"),
            new("/src/Legacy/Legacy.csproj", "Legacy", ProjectClassifier.ProjectKind.Legacy, "classifier reason"),
            new("/src/Odd/Odd.sqlproj", "Odd", ProjectClassifier.ProjectKind.Unknown, "Unrecognised project format."),
        ];
        var failures = new[]
        {
            "Msbuild failed when processing the file '/src/Legacy/Legacy.csproj': MSB3644.",
            "Unresolved reference in '/src/Loaded/Loaded.csproj'.",
            "Unrelated warning.",
        };

        var result = SolutionLoader.RemoveUnloadedProjects(
            new SolutionLoader.SolutionOpen(solution, workspace, [], []), toCheck, failures);

        Assert.Equal(["Loaded"], result.Solution.Projects.Select(p => p.Name));
        // Only the failure of the legacy project that stayed loaded marks the load degraded.
        Assert.Equal(["Unresolved reference in '/src/Loaded/Loaded.csproj'."], result.LoadDiagnostics);
        Assert.Collection(
            result.Skipped.OrderBy(e => e.Name, StringComparer.Ordinal),
            legacy =>
            {
                Assert.Equal("Legacy", legacy.Name);
                Assert.Equal("Legacy", legacy.Kind);
                Assert.Contains("MSB3644", legacy.Reason, StringComparison.Ordinal);
            },
            odd =>
            {
                Assert.Equal("Odd", odd.Name);
                Assert.Equal("Unknown", odd.Kind);
                Assert.Equal("Unrecognised project format.", odd.Reason);
            });
    }

    [Fact]
    public void RemoveUnloadedProjects_LegacyProjectWithoutFrameworkReferences_IsSkippedWithTheCause()
    {
        using var workspace = new AdhocWorkspace();
        var solution = AddProject(workspace.CurrentSolution, "NoFramework", "/src/NoFramework/NoFramework.csproj", withDocument: true);
        solution = AddProject(solution, "Resolved", "/src/Resolved/Resolved.csproj", withDocument: true, withFramework: true);
        ProjectClassifier.ClassifiedProject[] projects =
        [
            new("/src/NoFramework/NoFramework.csproj", "NoFramework", ProjectClassifier.ProjectKind.Legacy, null),
            new("/src/Resolved/Resolved.csproj", "Resolved", ProjectClassifier.ProjectKind.Legacy, null),
        ];

        var result = SolutionLoader.RemoveUnloadedProjects(
            new SolutionLoader.SolutionOpen(solution, workspace, [], []), projects,
            ["Msbuild failed when processing the file '/src/NoFramework/NoFramework.csproj': MSB3644."]);

        Assert.Equal(["Resolved"], result.Solution.Projects.Select(p => p.Name));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("NoFramework", skipped.Name);
        Assert.Equal("Legacy", skipped.Kind);
        Assert.Contains("framework references", skipped.Reason, StringComparison.Ordinal);
        Assert.Contains("MSB3644", skipped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveUnloadedProjects_SdkStylePlaceholder_IsSkippedWhileLoadedSdkStyleProjectIsKept()
    {
        using var workspace = new AdhocWorkspace();
        var solution = AddProject(workspace.CurrentSolution, "Broken", "/src/Broken/Broken.csproj", withDocument: false);
        solution = AddProject(solution, "Sdk", "/src/Sdk/Sdk.csproj", withDocument: true);
        ProjectClassifier.ClassifiedProject[] projects =
        [
            new("/src/Broken/Broken.csproj", "Broken", ProjectClassifier.ProjectKind.SdkStyle, null),
            new("/src/Sdk/Sdk.csproj", "Sdk", ProjectClassifier.ProjectKind.SdkStyle, null),
        ];

        var result = SolutionLoader.RemoveUnloadedProjects(
            new SolutionLoader.SolutionOpen(solution, workspace, [], []), projects,
            ["Msbuild failed when processing the file '/src/Broken/Broken.csproj': invalid condition."]);

        Assert.Equal(["Sdk"], result.Solution.Projects.Select(p => p.Name));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("Broken", skipped.Name);
        Assert.Equal("SdkStyle", skipped.Kind);
        Assert.Contains("invalid condition", skipped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void HasFrameworkReference_RecognisesCoreLibrariesByFileName()
    {
        using var workspace = new AdhocWorkspace();
        var none = AddProject(workspace.CurrentSolution, "None", "/src/None.csproj", withDocument: true);
        var with = AddProject(none, "With", "/src/With.csproj", withDocument: true, withFramework: true);

        Assert.False(SolutionLoader.HasFrameworkReference(with.Projects.Single(p => p.Name == "None")));
        Assert.True(SolutionLoader.HasFrameworkReference(with.Projects.Single(p => p.Name == "With")));
    }

    [WindowsOnlyFact]
    public async Task OpenAsync_LegacyProjectWithMissingImportAndNoTargetingPack_IsReportedAsSkippedOnWindows()
    {
        var solutionPath = Path.Combine(Path.GetDirectoryName(LegacySolutionPath)!, "MissingImport.slnx");

        var open = await new SolutionLoader().OpenAsync(solutionPath);
        var (solution, workspace, skipped) = open;
        using var _ = workspace;

        var diagnostic = "failures=[" + string.Join(" | ", open.LoadDiagnostics) + "] " + string.Join("; ", solution.Projects.Select(p =>
            $"{p.Name}: docs={p.DocumentIds.Count} out={p.OutputFilePath} refs=[{string.Join(",", p.MetadataReferences.OfType<PortableExecutableReference>().Select(r => Path.GetFileName(r.FilePath)))}]"));
        Assert.True(solution.Projects.Count() == 1, diagnostic);
        Assert.Equal("LoadableLegacyLib", Assert.Single(solution.Projects).Name);
        var entry = Assert.Single(skipped);
        Assert.Equal("MissingImportLib", entry.Name);
        Assert.Equal("Legacy", entry.Kind);
    }

    [Fact]
    public void RemoveUnloadedProjects_NothingToCheck_ReturnsTheSolutionUnchanged()
    {
        using var workspace = new AdhocWorkspace();
        var open = new SolutionLoader.SolutionOpen(workspace.CurrentSolution, workspace, [], []);

        var result = SolutionLoader.RemoveUnloadedProjects(open, [], []);

        Assert.Equal(open, result);
    }

    private static Solution AddProject(
        Solution solution, string name, string filePath, bool withDocument, bool withFramework = false)
    {
        var projectId = ProjectId.CreateNewId(name);
        solution = solution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Default, name, name, LanguageNames.CSharp, filePath));
        if (withFramework)
            solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return withDocument ? solution.AddDocument(DocumentId.CreateNewId(projectId), "A.cs", "class A {}") : solution;
    }

    [Fact]
    public void LegacyFallbackReason_NamesTheCauseAndDoesNotAskForAnotherUnfilteredLoad()
    {
        var reason = SolutionLoader.LegacyFallbackReason("the solution-level load timed out");

        Assert.Contains("non-SDK", reason, StringComparison.Ordinal);
        Assert.Contains("the solution-level load timed out", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("unfiltered", reason, StringComparison.OrdinalIgnoreCase);
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
