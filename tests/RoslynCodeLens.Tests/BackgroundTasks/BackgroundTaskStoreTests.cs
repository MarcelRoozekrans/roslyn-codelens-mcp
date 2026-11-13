using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests.BackgroundTasks;

public class BackgroundTaskStoreTests
{
    [Fact]
    public async Task WaitAsync_ReturnsEarly_WhenTaskCompletes()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool", async _ =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            return "payload";
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await store.WaitAsync(info.TaskId, TimeSpan.FromSeconds(30), CancellationToken.None)
            .ConfigureAwait(false);
        sw.Stop();

        Assert.NotNull(result);
        Assert.Equal(BackgroundTaskStatus.Succeeded, result!.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"waited {sw.Elapsed}");
    }

    [Fact]
    public async Task WaitAsync_ReturnsRunning_AfterTimeout()
    {
        using var store = new BackgroundTaskStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = store.Start("test_tool", async ct =>
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return null;
        });

        var result = await store.WaitAsync(info.TaskId, TimeSpan.FromMilliseconds(100), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(BackgroundTaskStatus.Running, result!.Status);
        gate.SetResult();
        await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
    }

    [Fact]
    public async Task WaitAsync_ZeroTimeout_ReturnsImmediately()
    {
        using var store = new BackgroundTaskStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = store.Start("test_tool", async ct =>
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return null;
        });

        var result = await store.WaitAsync(info.TaskId, TimeSpan.Zero, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(BackgroundTaskStatus.Running, result!.Status);
        gate.SetResult();
        await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
    }

    [Fact]
    public async Task WaitAsync_UnknownTaskId_ReturnsNull()
    {
        using var store = new BackgroundTaskStore();
        var result = await store.WaitAsync("bg-nope-foo-bar", TimeSpan.FromSeconds(1), CancellationToken.None)
            .ConfigureAwait(false);
        Assert.Null(result);
    }

    [Fact]
    public async Task WaitAsync_Cancellation_EndsTheWait()
    {
        using var store = new BackgroundTaskStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var info = store.Start("test_tool", async ct =>
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return null;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WaitAsync(info.TaskId, TimeSpan.FromSeconds(30), cts.Token));

        gate.SetResult();
        await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(int.MaxValue, 50)]
    public void ClampWait_ClampsToZeroAndCap(int requested, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), BackgroundTaskStore.ClampWait(requested));
    }

    [Fact]
    public async Task Start_AssignsHumanReadableTaskId()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("rebuild_solution",
            _ => Task.FromResult<object?>("done"));
        Assert.StartsWith("bg-rebuild_solution-", info.TaskId, StringComparison.Ordinal);
        await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
    }

    [Fact]
    public async Task Start_SucceededTask_PreservesResult()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool",
            _ => Task.FromResult<object?>("payload"));

        var terminal = await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
        Assert.Equal(BackgroundTaskStatus.Succeeded, terminal.Status);
        Assert.Equal("payload", terminal.Result);
    }

    [Fact]
    public async Task Start_FailedTask_McpToolException_PreservesCodeAndMessage()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool",
            _ => throw new McpToolException(
                ToolErrorCode.SymbolNotFound, "X"));

        var terminal = await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
        Assert.Equal(BackgroundTaskStatus.Failed, terminal.Status);
        Assert.Equal("SymbolNotFound", terminal.ErrorCode);
        Assert.Equal("X", terminal.ErrorMessage);
    }

    [Fact]
    public async Task Start_FailedTask_GenericException_DefaultsToInternalCode()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool",
            _ => throw new InvalidOperationException("boom"));

        var terminal = await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
        Assert.Equal(BackgroundTaskStatus.Failed, terminal.Status);
        Assert.Equal("Internal", terminal.ErrorCode);
        Assert.Equal("boom", terminal.ErrorMessage);
    }

    [Fact]
    public void Get_UnknownTaskId_ReturnsNull()
    {
        using var store = new BackgroundTaskStore();
        Assert.Null(store.Get("bg-nope-foo-bar"));
    }

    [Fact]
    public async Task ListRunning_IncludesRecentlyCompleted()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool",
            _ => Task.FromResult<object?>("done"));
        var terminal = await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);

        // The just-completed task should still appear in ListRunning (within 5-min window).
        Assert.Contains(store.ListRunning(), t => t.TaskId == info.TaskId);
    }

    private static async Task<BackgroundTaskInfo> WaitForTerminal(
        BackgroundTaskStore store, string taskId, int maxMs = 2000)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start).TotalMilliseconds < maxMs)
        {
            var info = store.Get(taskId)!;
            if (info.Status != BackgroundTaskStatus.Running) return info;
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Task {taskId} did not reach terminal state.");
    }
}
