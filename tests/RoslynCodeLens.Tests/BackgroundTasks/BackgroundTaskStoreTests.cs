using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests.BackgroundTasks;

public class BackgroundTaskStoreTests
{
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
