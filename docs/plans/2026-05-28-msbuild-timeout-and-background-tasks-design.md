# MSBuild Timeout + Background Task Store — Design

**Date:** 2026-05-28
**Status:** Approved, ready for implementation plan
**Inspiration:** Patterns observed in `GerardSmit/RoslynSense` (no license — patterns only, no code copied).

**Motivation:** Two correlated correctness/UX gaps in long-running operations:

1. **MSBuild `OpenProjectAsync` can wedge.** The `BuildHost-net472` subprocess hangs on certain legacy or malformed projects, blocking the entire solution load indefinitely with no recovery. Today we have no timeout — a single bad project can hang the server boot or a `rebuild_solution` call forever.
2. **No way to run long tools without blocking the MCP response.** A solution rebuild can take minutes; the MCP client times out waiting for `rebuild_solution` to return, even though the server is making progress. Some clients lack a robust mid-request cancellation story, so they fail noisily on long-running tools.

## Goals

- Bound the latency of per-project MSBuild project load with a configurable timeout. Timeout-skipped projects appear in the existing `SkippedProjects` mechanism.
- Provide a small, explicit set of MCP tools that let callers initiate long-running work, get a task ID back immediately, and poll for completion.
- Keep the surface minimal — three new tools, one allowlist, no per-tool plumbing churn.

## Non-goals

- No persistent task storage. The MCP server is a single-process stdio host; if the process dies, in-flight background tasks die with it. This matches the MCP-server lifetime model.
- No cancellation of in-flight tasks via a new tool. Each task gets its own `CancellationTokenSource`, but there's no `cancel_task` tool in this PR (YAGNI; add when a real need surfaces).
- No structured logging integration. The audit's "restore structured logging" item (#7) is a separate PR.
- No diagnostic caching layer. Item #3 from the RoslynSense review was deferred — no proven perf problem with `get_diagnostics` today.
- No solution-wide load timeout — per-project is sufficient. A solution with 100 projects and a 300s per-project timeout means worst-case ~8h to fail, but realistically the first hang causes everything after it to skip cleanly. If solution-wide ceiling becomes useful, add it later.

## Architecture

### Section 1 — Per-project MSBuild timeout

**Touch point:** wherever `MSBuildWorkspace.OpenProjectAsync` is called per-project during solution load. Likely `SolutionLoader.cs` (verify during impl).

**Mechanism:** wrap each call with a linked CTS combining the caller's `ct` and a per-project timeout deadline:

```csharp
private const int DefaultOpenProjectTimeoutSec = 300;

private static int GetOpenProjectTimeoutSec() =>
    int.TryParse(Environment.GetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS"),
        out var n) && n > 0
            ? n
            : DefaultOpenProjectTimeoutSec;

private static async Task<Project?> OpenProjectWithTimeoutAsync(
    MSBuildWorkspace workspace, string projectPath, CancellationToken outerCt)
{
    var timeoutSec = GetOpenProjectTimeoutSec();
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

    try
    {
        return await workspace.OpenProjectAsync(projectPath, cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
    {
        return null;   // timeout — caller records SkippedProject
    }
}
```

**OCE filter on `!outerCt.IsCancellationRequested`** mirrors the established `AnalyzerRunner.cs` pattern: timeout (internal) is suppressed; user cancellation propagates.

**Caller convention:** when `OpenProjectWithTimeoutAsync` returns `null`, add a `SkippedProjectInfo` entry with `Kind: "Timeout"` and `Reason: "Load exceeded Ns. Set ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS to override."`. The existing `list_solutions` tool already surfaces `SkippedProjects` — no new wire surface.

### Section 2 — Background task store + 3 MCP tools

**Component layout:**
- `src/RoslynCodeLens/BackgroundTasks/BackgroundTaskStore.cs` — singleton store with `Start` / `Get` / `ListRunning` + timer-driven eviction.
- `src/RoslynCodeLens/BackgroundTasks/BackgroundTask.cs` — internal record (TaskId, ToolName, StartedAt, CompletedAt?, Status, Result?, ErrorMessage?, ErrorCode?, Cts).
- `src/RoslynCodeLens/Models/BackgroundTaskInfo.cs` — wire-friendly projection (no `Cts`).
- `src/RoslynCodeLens/Models/BackgroundTaskStatus.cs` — enum: `Running`, `Succeeded`, `Failed`, `Cancelled`.
- `src/RoslynCodeLens/BackgroundTasks/BackgroundTaskAllowlist.cs` — `IReadOnlySet<string>` of tool names that can be wrapped. Initial content: `{ "rebuild_solution" }`.
- `src/RoslynCodeLens/Tools/StartBackgroundTaskTool.cs`
- `src/RoslynCodeLens/Tools/GetTaskStatusTool.cs`
- `src/RoslynCodeLens/Tools/ListRunningTasksTool.cs`

**Lifecycle:**
- `Start(toolName, work)` generates a slug like `bg-rebuild-bold-arch` from a small wordlist, queues the work as a `Task.Run`, returns the projection immediately.
- `Get(taskId)` returns the projection or `null`.
- `ListRunning()` filters terminal-state tasks older than 5 min from the listing (full eviction is 60 min).
- Eviction timer runs every minute; removes terminal-state entries past `EvictionAge` (60 min).

**`start_background_task` dispatcher** uses a small `switch` on `toolName`:

```csharp
return toolName switch
{
    "rebuild_solution" => store.Start("rebuild_solution",
        ct => RunRebuildSolutionAsync(manager, ct)),
    _ => throw new McpToolException(
        ToolErrorCode.InvalidArgument,
        $"Tool '{toolName}' does not support background execution.",
        new { allowedTools = BackgroundTaskAllowlist.AllowedTools.ToArray() }),
};
```

Adding a new tool to the allowlist = add a `case` plus the corresponding allowlist entry. Documented as the extension path in a comment near both files.

**Error handling inside background work:**

```csharp
try
{
    var result = await work(task.Cts.Token).ConfigureAwait(false);
    task.Status = BackgroundTaskStatus.Succeeded;
    task.Result = result;
}
catch (OperationCanceledException)
{
    task.Status = BackgroundTaskStatus.Cancelled;
}
catch (McpToolException mcpEx)
{
    task.Status = BackgroundTaskStatus.Failed;
    task.ErrorCode = mcpEx.Code.ToString();
    task.ErrorMessage = mcpEx.Message;
}
catch (Exception ex)
{
    task.Status = BackgroundTaskStatus.Failed;
    task.ErrorCode = nameof(ToolErrorCode.Internal);
    task.ErrorMessage = ex.Message;
}
finally
{
    task.CompletedAt = DateTimeOffset.UtcNow;
}
```

**`get_task_status` for unknown taskId** throws `McpToolException(InvalidArgument, "Unknown task id 'X'.")`. No new `ToolErrorCode` value introduced.

**`list_running_tasks` envelope:** uses our existing `ToolListResult<BackgroundTaskInfo>`. No special summary — list is naturally small.

### Cancellation
- The MCP framework injects `ct` into each tool's `Execute`. For `start_background_task`, cancelling the outer call before the task is queued cleanly aborts; cancelling after the task is queued is intentionally a no-op on the queued work (background tasks have their own lifetime — that's the whole point).
- Each `BackgroundTask.Cts` is independent of the MCP request. No external cancel pathway today (future `cancel_task` tool).

## Test strategy

- **`SolutionLoaderTimeoutTests`** — exercise the helper directly with a fake awaitable + short timeout (1s test, 2s delay), assert null return + SkippedProject entry recorded. Env-var override test. User-cancellation propagation test.
- **`BackgroundTaskStoreTests`** — 6 tests: slug format, running status, succeeded with result, failed preserves McpToolException code, unknown id returns null (Tool layer translates to thrown InvalidArgument), eviction after age.
- **`StartBackgroundTaskToolTests`** — unknown toolName throws InvalidArgument; allowlisted tool queues + returns task id.
- **`ListRunningTasksToolTests`** — envelope shape, terminal-state filter excludes recently-completed.

## Risks and accepted trade-offs

- **Server process death loses in-flight tasks.** No persistence. Documented in tool descriptions.
- **Result memory pressure.** 60-min eviction window bounds growth; misbehaving client missing the eviction window sees results disappear. Acceptable.
- **No way to cancel tasks from MCP.** Future work; YAGNI now.
- **MSBuild 300s default may cut legitimate large-project loads.** Env var override + SkippedProjects visibility provides escape hatch.
- **Allowlist is a constant, not config.** Adding a new tool requires a code change. Intentional — keeps the dispatcher type-safe and reviewable.

## Out of scope (intentional)

- Diagnostic caching (#3 from RoslynSense review) — deferred until profiled.
- Progress reporting via MCP's `notifications/progress` (#4 from earlier audit) — separate PR; the background-task pattern doesn't preclude it.
- Structured logging (#7) — separate PR.
- `cancel_task` tool — add when a real workflow needs it.
