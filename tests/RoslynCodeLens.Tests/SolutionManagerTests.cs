using Microsoft.CodeAnalysis.Text;

namespace RoslynCodeLens.Tests;

public class SolutionManagerTests : IAsyncLifetime
{
    private string _solutionPath = null!;

    public Task InitializeAsync()
    {
        _solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateAsync_LoadsSolutionAndResolver()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);

        Assert.NotNull(manager.GetLoadedSolution());
        Assert.False(manager.GetLoadedSolution().IsEmpty);
        Assert.NotNull(manager.GetResolver());
        manager.Dispose();
    }

    [Fact]
    public async Task GetResolver_ReturnsCachedInstance_WhenNotStale()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);
        var resolver1 = manager.GetResolver();
        var resolver2 = manager.GetResolver();

        Assert.Same(resolver1, resolver2);
        manager.Dispose();
    }

    [Fact]
    public void EnsureLoaded_ThrowsForEmptySolution()
    {
        var manager = SolutionManager.CreateEmpty();
        Assert.Throws<InvalidOperationException>(() => manager.EnsureLoaded());
        manager.Dispose();
    }

    // Issue #399: an empty workspace has two causes that need different messages. Reporting
    // "No .sln file found" for a solution that WAS found but whose projects all failed to open
    // sends the reader hunting for a missing file instead of at the actual load failure.
    [Fact]
    public void DescribeEmptyWorkspace_SaysNoSolution_WhenNonePassedOrDiscovered()
    {
        var message = SolutionManager.DescribeEmptyWorkspace(null, Array.Empty<SkippedProject>());

        Assert.Contains("No solution found", message, StringComparison.Ordinal);
        Assert.Contains(".slnx", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeEmptyWorkspace_NamesTheSolution_WhenItLoadedWithNoProjects()
    {
        var message = SolutionManager.DescribeEmptyWorkspace(
            @"C:/code/MyApp.slnx", Array.Empty<SkippedProject>());

        Assert.Contains("MyApp.slnx", message, StringComparison.Ordinal);
        Assert.DoesNotContain("No solution found", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeEmptyWorkspace_ReportsSkipReasons_WhenEveryProjectWasSkipped()
    {
        var skipped = new[]
        {
            new SkippedProject(@"C:/code/A.csproj", "A", "Legacy", "non-SDK project"),
            new SkippedProject(@"C:/code/B.csproj", "B", "Failed", "design-time build failed"),
        };

        var message = SolutionManager.DescribeEmptyWorkspace(@"C:/code/MyApp.slnx", skipped);

        Assert.Contains("MyApp.slnx", message, StringComparison.Ordinal);
        Assert.Contains("all 2 project(s) were skipped", message, StringComparison.Ordinal);
        Assert.Contains("A: non-SDK project", message, StringComparison.Ordinal);
        Assert.Contains("B: design-time build failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeEmptyWorkspace_TruncatesLongSkipLists()
    {
        var skipped = Enumerable.Range(0, 8)
            .Select(i => new SkippedProject($"C:/code/P{i}.csproj", $"P{i}", "Failed", "boom"))
            .ToArray();

        var message = SolutionManager.DescribeEmptyWorkspace(@"C:/code/MyApp.slnx", skipped);

        Assert.Contains("all 8 project(s) were skipped", message, StringComparison.Ordinal);
        Assert.Contains("(and 3 more)", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_ReturnsBeforeCompilationCompletes()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);

        // After warmup, resolver should have data
        await manager.WaitForWarmupAsync();
        var resolver = manager.GetResolver();

        Assert.True(resolver.AllTypes.Count > 0);
        manager.Dispose();
    }

    [Fact]
    public async Task CommitDocumentTexts_ImmediateQueriesSeeNewText_WithoutAnyWatcherEvent()
    {
        // Finding 5: after rename_symbol writes files, the manager's in-memory snapshot must
        // reflect the new text immediately — no disk write happens here at all, so the file
        // watcher can never deliver this change; only CommitDocumentTextsAsync can.
        var manager = await SolutionManager.CreateAsync(_solutionPath);
        try
        {
            await manager.WaitForWarmupAsync();
            var before = manager.GetAnalysisContext();
            Assert.Empty(before.Resolver.FindSymbols("CommittedProbe"));

            var doc = before.Loaded.Solution.Projects
                .First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal))
                .Documents.First(d => d.FilePath != null);
            var text = await doc.GetTextAsync();
            var newText = SourceText.From(
                text + "\n\npublic class CommittedProbe { }\n", text.Encoding);

            await manager.CommitDocumentTextsAsync([(doc.Id, newText)]);

            var after = manager.GetAnalysisContext();
            // Resolver rebuilt from the committed snapshot sees the new type immediately.
            Assert.NotEmpty(after.Resolver.FindSymbols("CommittedProbe"));
            // The solution text and the cached compilation were both refreshed.
            var committedText = await after.Loaded.Solution.GetDocument(doc.Id)!.GetTextAsync();
            Assert.Contains("CommittedProbe", committedText.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                after.Loaded.Compilations[doc.Project.Id].SyntaxTrees,
                t => t.ToString().Contains("CommittedProbe", StringComparison.Ordinal));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task CommitDocumentTexts_UnknownDocumentId_IsANoOp()
    {
        // After a full reload mints fresh DocumentIds, a commit computed against the old
        // snapshot must be skipped gracefully (disk already holds the text; the reload read it).
        var manager = await SolutionManager.CreateAsync(_solutionPath);
        try
        {
            await manager.WaitForWarmupAsync();
            var before = manager.GetAnalysisContext();

            var foreignId = Microsoft.CodeAnalysis.DocumentId.CreateNewId(
                Microsoft.CodeAnalysis.ProjectId.CreateNewId());
            await manager.CommitDocumentTextsAsync(
                [(foreignId, SourceText.From("public class Ghost { }"))]);

            var after = manager.GetAnalysisContext();
            Assert.Same(before.Loaded, after.Loaded);   // nothing applied — no snapshot swap
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task GetResolver_AwaitsWarmupIfNotReady()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);

        // GetResolver should block until warmup is done and return valid resolver
        var resolver = manager.GetResolver();
        Assert.True(resolver.AllTypes.Count > 0);
        manager.Dispose();
    }
}
