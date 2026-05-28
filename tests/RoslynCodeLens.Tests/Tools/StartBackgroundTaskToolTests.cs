using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class StartBackgroundTaskToolTests
{
    [Fact]
    public void Execute_UnknownTool_ThrowsInvalidArgument()
    {
        using var store = new BackgroundTaskStore();
        var manager = MultiSolutionManager.CreateEmpty();
        var ex = Assert.Throws<McpToolException>(() =>
            StartBackgroundTaskTool.Execute(manager, store, "not_a_tool"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        manager.Dispose();
    }

    [Fact]
    public void Execute_AllowedTool_ReturnsTaskIdAndQueues()
    {
        using var store = new BackgroundTaskStore();
        var manager = MultiSolutionManager.CreateEmpty();
        var info = StartBackgroundTaskTool.Execute(manager, store, "rebuild_solution");
        Assert.StartsWith("bg-rebuild_solution-", info.TaskId, StringComparison.Ordinal);
        Assert.Equal("rebuild_solution", info.ToolName);
        manager.Dispose();
    }
}
