using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class StartBackgroundTaskTool
{
    [McpServerTool(Name = "start_background_task"),
     Description("Queue a long-running tool to run in the background. Returns a taskId; " +
                 "poll with get_task_status. Allowed tools: rebuild_solution.")]
    public static BackgroundTaskInfo Execute(
        MultiSolutionManager manager,
        BackgroundTaskStore store,
        [Description("Tool name to run in the background. Currently allowed: rebuild_solution.")]
            string toolName)
    {
        if (!BackgroundTaskAllowlist.AllowedTools.Contains(toolName))
        {
            throw new McpToolException(
                ToolErrorCode.InvalidArgument,
                $"Tool '{toolName}' does not support background execution. " +
                $"Allowed tools: {string.Join(", ", BackgroundTaskAllowlist.AllowedTools)}.",
                new { allowedTools = BackgroundTaskAllowlist.AllowedTools.ToArray() });
        }

        return toolName switch
        {
            "rebuild_solution" => store.Start("rebuild_solution",
                async _ =>
                {
                    var (count, elapsed) = await manager.ForceReloadAsync().ConfigureAwait(false);
                    return new { projectCount = count, elapsedMs = elapsed.TotalMilliseconds };
                }),
            _ => throw new McpToolException(
                ToolErrorCode.Internal,
                $"Allowlist contains '{toolName}' but no dispatch case exists. Add a switch arm in StartBackgroundTaskTool."),
        };
    }
}
