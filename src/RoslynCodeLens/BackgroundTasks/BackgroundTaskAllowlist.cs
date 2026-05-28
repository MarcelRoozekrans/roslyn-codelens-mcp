namespace RoslynCodeLens.BackgroundTasks;

internal static class BackgroundTaskAllowlist
{
    /// <summary>
    /// Tool names that can be wrapped by <c>start_background_task</c>.
    /// To add a new tool, append its name here AND add a corresponding
    /// case in <c>StartBackgroundTaskTool.Execute</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedTools =
        new HashSet<string>(StringComparer.Ordinal) { "rebuild_solution" };
}
