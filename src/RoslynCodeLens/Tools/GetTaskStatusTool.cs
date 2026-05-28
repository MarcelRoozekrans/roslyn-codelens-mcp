using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetTaskStatusTool
{
    [McpServerTool(Name = "get_task_status"),
     Description("Get the current status of a background task by its taskId.")]
    public static BackgroundTaskInfo Execute(
        BackgroundTaskStore store,
        [Description("The taskId returned by start_background_task")] string taskId)
    {
        var info = store.Get(taskId);
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
