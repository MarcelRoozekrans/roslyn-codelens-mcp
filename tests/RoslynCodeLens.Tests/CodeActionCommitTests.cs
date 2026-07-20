using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

/// <summary>
/// The post-write commit that publishes an applied code action to the in-memory snapshot, so a
/// query issued immediately afterwards sees the new text instead of waiting out the file
/// watcher's debounce (the stale-read gap closed for rename_symbol in #300).
/// </summary>
public class CodeActionCommitTests
{
    private static SolutionWriteResult Written(params string[] contents)
    {
        var projectId = ProjectId.CreateNewId();
        var documents = contents
            .Select(c => (Id: DocumentId.CreateNewId(projectId), Text: SourceText.From(c)))
            .ToList();
        return new SolutionWriteResult(Written: true, StaleFiles: [], Documents: documents);
    }

    [Fact]
    public async Task Commit_ReceivesExactlyTheWrittenDocuments()
    {
        var write = Written("class A { }", "class B { }");
        IReadOnlyList<(DocumentId Id, SourceText Text)>? received = null;

        var warning = await CodeActionRunner.CommitAsync(
            (docs, _) => { received = docs; return Task.CompletedTask; },
            write, CancellationToken.None);

        Assert.Null(warning);
        Assert.Equal(write.Documents, received);
    }

    [Fact]
    public async Task NoHook_IsANoOp()
        => Assert.Null(await CodeActionRunner.CommitAsync(null, Written("class A { }"), CancellationToken.None));

    [Fact]
    public async Task NothingWritten_SkipsTheHook()
    {
        var called = false;
        var empty = new SolutionWriteResult(Written: true, StaleFiles: [], Documents: []);

        var warning = await CodeActionRunner.CommitAsync(
            (_, _) => { called = true; return Task.CompletedTask; }, empty, CancellationToken.None);

        Assert.False(called);
        Assert.Null(warning);
    }

    /// <summary>
    /// The files are already on disk when the commit runs, so a refresh failure must not turn a
    /// completed apply into a reported failure — it degrades to a warning and the watcher converges.
    /// </summary>
    [Fact]
    public async Task CommitFailure_DegradesToAWarning()
    {
        var warning = await CodeActionRunner.CommitAsync(
            (_, _) => throw new InvalidOperationException("snapshot busy"),
            Written("class A { }"), CancellationToken.None);

        Assert.NotNull(warning);
        Assert.Contains("snapshot busy", warning, StringComparison.Ordinal);
        Assert.Contains("rebuild_solution", warning, StringComparison.Ordinal);
    }

    /// <summary>Cancellation is a real abort, not a warning — it must surface to the caller.</summary>
    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodeActionRunner.CommitAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            Written("class A { }"), cts.Token));
    }
}
