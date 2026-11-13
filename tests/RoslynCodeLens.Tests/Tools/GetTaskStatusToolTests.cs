using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetTaskStatusToolTests
{
    [Fact]
    public async Task Execute_UnknownTaskId_ThrowsInvalidArgument()
    {
        using var store = new BackgroundTaskStore();
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            GetTaskStatusTool.Execute(store, "bg-nope-foo-bar"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Execute_KnownTaskId_ReturnsCurrentStatus()
    {
        using var store = new BackgroundTaskStore();
        var started = store.Start("test_tool", _ => Task.FromResult<object?>("done"));
        await Task.Delay(100).ConfigureAwait(false);
        var info = await GetTaskStatusTool.Execute(store, started.TaskId);
        Assert.Equal(started.TaskId, info.TaskId);
    }

    [Fact]
    public async Task Execute_UnknownTaskId_WithWait_StillThrowsInvalidArgument()
    {
        using var store = new BackgroundTaskStore();
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            GetTaskStatusTool.Execute(store, "bg-nope-foo-bar", waitSeconds: 5));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Execute_WaitSeconds_ReturnsTerminalInfo_WhenTaskCompletesDuringWait()
    {
        using var store = new BackgroundTaskStore();
        var started = store.Start("test_tool", async _ =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            return "done";
        });
        var info = await GetTaskStatusTool.Execute(store, started.TaskId, waitSeconds: 30);
        Assert.Equal(BackgroundTaskStatus.Succeeded, info.Status);
        Assert.Equal("done", info.Result);
    }

    [Fact]
    public async Task Execute_NegativeWaitSeconds_ReturnsImmediately()
    {
        using var store = new BackgroundTaskStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = store.Start("test_tool", async ct =>
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return null;
        });
        var info = await GetTaskStatusTool.Execute(store, started.TaskId, waitSeconds: -3);
        Assert.Equal(BackgroundTaskStatus.Running, info.Status);
        gate.SetResult();
    }
}
