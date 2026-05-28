using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetTaskStatusToolTests
{
    [Fact]
    public void Execute_UnknownTaskId_ThrowsInvalidArgument()
    {
        using var store = new BackgroundTaskStore();
        var ex = Assert.Throws<McpToolException>(() =>
            GetTaskStatusTool.Execute(store, "bg-nope-foo-bar"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Execute_KnownTaskId_ReturnsCurrentStatus()
    {
        using var store = new BackgroundTaskStore();
        var started = store.Start("test_tool", _ => Task.FromResult<object?>("done"));
        await Task.Delay(100).ConfigureAwait(false);
        var info = GetTaskStatusTool.Execute(store, started.TaskId);
        Assert.Equal(started.TaskId, info.TaskId);
    }
}
