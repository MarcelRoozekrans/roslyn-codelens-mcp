using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class LoadSolutionToolTests
{
    private readonly string _solutionPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));

    [Fact]
    public async Task Execute_FileNotFound_ThrowsFileNotFoundException()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => LoadSolutionTool.Execute(manager, store, "/does/not/exist.sln"));
    }

    [Fact]
    public async Task Execute_ValidPath_ReturnsLoadedMessage()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();

        var result = (string)await LoadSolutionTool.Execute(manager, store, _solutionPath);

        Assert.Contains("Loaded", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TestSolution", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_Background_BadPath_ThrowsSynchronously()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();

        // Validation must happen before any task is queued, so the caller gets
        // immediate feedback rather than discovering the failure only by polling.
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => LoadSolutionTool.Execute(manager, store, "/does/not/exist.sln", background: true));
    }

    [Fact]
    public async Task Execute_Background_ReturnsRunningTaskThatSucceeds()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        using var store = new BackgroundTaskStore();

        var queued = (BackgroundTaskInfo)await LoadSolutionTool.Execute(
            manager, store, _solutionPath, background: true);

        // Returns a handle immediately — the call itself does not block on the load.
        Assert.Equal("load_solution", queued.ToolName);
        Assert.False(string.IsNullOrEmpty(queued.TaskId));

        var final = await PollUntilTerminalAsync(store, queued.TaskId);

        Assert.Equal(BackgroundTaskStatus.Succeeded, final.Status);
        Assert.NotNull(final.Result);

        // The loaded solution becomes active only once the task finishes.
        Assert.True(manager.GetLoadedSolution().Solution.Projects.Any());
    }

    private static async Task<BackgroundTaskInfo> PollUntilTerminalAsync(BackgroundTaskStore store, string taskId)
    {
        for (var i = 0; i < 600; i++)   // up to ~60s
        {
            var info = store.Get(taskId);
            Assert.NotNull(info);
            if (info!.Status != BackgroundTaskStatus.Running)
                return info;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Background task {taskId} did not complete in time.");
    }
}
