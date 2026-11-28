using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class StartBackgroundTaskToolTests
{
    private static readonly string ActiveKey = Path.GetFullPath("/tmp/a/A.sln");

    [Fact]
    public void Execute_UnknownSolution_ThrowsBeforeQueueing()
    {
        using var store = new BackgroundTaskStore();
        var manager = MultiSolutionManager.CreateForTesting(
            new Dictionary<string, SolutionManager>(StringComparer.Ordinal) { [ActiveKey] = SolutionManager.CreateEmpty() }, ActiveKey);

        var ex = Assert.Throws<McpToolException>(() =>
            StartBackgroundTaskTool.Execute(manager, store, "rebuild_solution", solution: "Nope"));

        Assert.Equal(ToolErrorCode.ProjectNotFound, ex.Code);
        Assert.Empty(store.ListRunning());
        manager.Dispose();
    }

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
        // The solution is resolved when the task is queued, so one has to be active.
        var manager = MultiSolutionManager.CreateForTesting(
            new Dictionary<string, SolutionManager>(StringComparer.Ordinal) { [ActiveKey] = SolutionManager.CreateEmpty() }, ActiveKey);
        var info = StartBackgroundTaskTool.Execute(manager, store, "rebuild_solution");
        Assert.StartsWith("bg-rebuild_solution-", info.TaskId, StringComparison.Ordinal);
        Assert.Equal("rebuild_solution", info.ToolName);
        manager.Dispose();
    }
}
