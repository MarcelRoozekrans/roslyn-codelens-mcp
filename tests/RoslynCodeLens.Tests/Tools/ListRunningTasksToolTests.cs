using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class ListRunningTasksToolTests
{
    [Fact]
    public void Execute_EmptyStore_ReturnsEmptyEnvelope()
    {
        using var store = new BackgroundTaskStore();
        var result = ListRunningTasksTool.Execute(store);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Execute_WithRunningTask_AppearsInList()
    {
        using var store = new BackgroundTaskStore();
        var started = store.Start("test_tool",
            ct => Task.Delay(5000, ct).ContinueWith(_ => (object?)"done"));

        var result = ListRunningTasksTool.Execute(store);
        Assert.Contains(result.Items, t => t.TaskId == started.TaskId);
    }
}
