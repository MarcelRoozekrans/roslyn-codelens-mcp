using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ListRunningTasksTool
{
    private const int DefaultLimit = 50;

    [McpServerTool(Name = "list_running_tasks"),
     Description("List background tasks that are running or completed within the last 5 minutes.")]
    public static ToolListResult<BackgroundTaskInfo> Execute(
        BackgroundTaskStore store,
        [Description("Maximum number of items to return (default: 50)")] int? limit = null)
    {
        var items = store.ListRunning();
        return ToolListResult.Create(items, limit ?? DefaultLimit);
    }
}
