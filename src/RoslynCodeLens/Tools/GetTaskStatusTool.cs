using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetTaskStatusTool
{
    [McpServerTool(Name = "get_task_status"),
     Description("Get the current status of a background task by its taskId. " +
                 "Pass waitSeconds to wait on the server while the task is Running: the call returns as soon as " +
                 "the task finishes, or after waitSeconds with the task still Running. Prefer one call with a " +
                 "generous waitSeconds (the 50 second cap) over polling. Values above 50 are clamped to 50.")]
    public static async Task<BackgroundTaskInfo> Execute(
        BackgroundTaskStore store,
        [Description("The taskId returned by start_background_task")] string taskId,
        [Description("Seconds to wait for a running task to finish. Default 0 returns immediately; negative is treated as 0; values above 50 are clamped to 50.")] int waitSeconds = 0,
        CancellationToken ct = default)
    {
        var info = await store.WaitAsync(taskId, BackgroundTaskStore.ClampWait(waitSeconds), ct)
            .ConfigureAwait(false);
        if (info is null)
        {
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"Unknown task id '{taskId}'. Either the id is wrong or the task has been evicted.",
                new { taskId });
        }
        return info;
    }
}
