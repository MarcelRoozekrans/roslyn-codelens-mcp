using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

/// <summary>
/// The post-write commit shared by every writing tool (apply_code_action, rename_symbol): it
/// publishes written text to the in-memory snapshot so a query issued immediately afterwards
/// sees it instead of waiting out the file watcher debounce (the #300 stale-read gap).
/// </summary>
public class SolutionCommitTests
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

        var warning = await SolutionChangeWriter.CommitAsync(
            (docs, _) => { received = docs; return Task.CompletedTask; },
            write, CancellationToken.None);

        Assert.Null(warning);
        Assert.Equal(write.Documents, received);
    }

    [Fact]
    public async Task NoHook_IsANoOp()
        => Assert.Null(await SolutionChangeWriter.CommitAsync(null, Written("class A { }"), CancellationToken.None));

    [Fact]
    public async Task NothingWritten_SkipsTheHook()
    {
        var called = false;
        var empty = new SolutionWriteResult(Written: true, StaleFiles: [], Documents: []);

        var warning = await SolutionChangeWriter.CommitAsync(
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
        var warning = await SolutionChangeWriter.CommitAsync(
            (_, _) => throw new InvalidOperationException("snapshot busy"),
            Written("class A { }"), CancellationToken.None);

        Assert.NotNull(warning);
        Assert.Contains("snapshot busy", warning, StringComparison.Ordinal);
        Assert.Contains("rebuild_solution", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancellation here must NOT surface as a cancelled operation: the files are already written
    /// and that cannot be undone, so reporting "cancelled" would tell the caller nothing happened
    /// and invite a retry over edits that already landed. It degrades to a warning like any other
    /// commit failure.
    /// </summary>
    [Fact]
    public async Task CancellationAfterTheWrite_DegradesToAWarning()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var warning = await SolutionChangeWriter.CommitAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            Written("class A { }"), cts.Token);

        Assert.NotNull(warning);
        Assert.Contains("cancelled", warning, StringComparison.Ordinal);
        Assert.Contains("written", warning, StringComparison.Ordinal);
    }
}
