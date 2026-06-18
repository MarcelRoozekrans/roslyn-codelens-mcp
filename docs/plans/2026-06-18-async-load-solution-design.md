# Async `load_solution` — design

**Status:** approved (2026-06-18)
**Issue:** [#232](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/232) — deferred item (b)
**Theme:** Startup & loading performance (BACKLOG section 4)
**Builds on:** [project-filter](2026-06-17-project-filter-load-solution-design.md), [parallel loader](2026-06-18-parallel-project-loader-design.md)

## Problem

`load_solution` blocks until the solution is fully open **and** compiled. On a 400-project solution that is minutes; the MCP client typically times out at 30 s, and even when it doesn't, the agent is wedged on a single tool call and can't do anything else (the original report: *"load_solution took about 10 minutes, blocking my agent"*).

The filter (item a's companion) shrinks *what* is loaded; this item removes the *blocking* — let the agent fire-and-poll so it can keep working while a large open is in flight.

## The infrastructure already exists

The backlog sketch predates the background-task system now in the codebase:

- `BackgroundTaskStore.Start(toolName, work)` runs `work` on a `Task.Run`, returns a `BackgroundTaskInfo` immediately (`{ taskId, status: Running, … }`), captures the result/error on completion, and evicts old entries.
- `get_task_status(taskId)` polls it; `list_running_tasks` lists in-flight ones.
- `rebuild_solution` already uses this via `start_background_task`.

This **is** the "return a handle, poll for status" pattern the backlog wanted (`loadId` → `taskId`, `get_load_status` → `get_task_status`). Reusing it is preferable to inventing a parallel `get_load_status`/`loadId` surface: one mental model for the agent, one eviction/lifecycle implementation, zero new tools.

`start_background_task` itself can't carry `load_solution`'s arguments (it only takes a `toolName` string), so the trigger is a `background` flag on `load_solution` rather than an allowlist entry.

## API surface

`load_solution` gains one optional parameter; default behaviour unchanged.

```jsonc
{
  "path": "/abs/huge.sln",
  "include": ["MyApp.*"],     // existing
  "rootProjects": ["MyApp.Api"], // existing
  "background": true            // NEW — default false
}
```

- `background: false` (default) — synchronous, returns the existing `"Loaded N project(s) from: …"` string. No behavioural change.
- `background: true` — validates `path` synchronously (so a bad path fails fast), then starts the load on `BackgroundTaskStore` and returns a `BackgroundTaskInfo` immediately. The agent polls `get_task_status(taskId)`:
  - `Running` → still loading.
  - `Succeeded` → `Result` carries `{ path, loadedProjects, skippedProjects, skipped[] }`.
  - `Failed` → `ErrorCode` / `ErrorMessage` (e.g. a filter matching no project).

The tool return type widens from `string` to `object` to carry either shape.

## Why no `SolutionManager` / `EnsureLoaded` changes are needed

The deferred-item note feared this would touch every tool's `EnsureLoaded()`. It doesn't, because of *when the new solution becomes active*:

`MultiSolutionManager.LoadSolutionAsync` only sets `_activeKey` to the new path **after** the structural open completes (inside the background work). So while a background load is in flight:

- The active solution is whatever it was before (or none) — other tools operate on it normally and are **never blocked** by the in-flight load.
- The in-flight load is observable only through its `taskId`.

The compilation (warmup) still runs in `SolutionManager`'s existing background `_warmupTask`; the background load work awaits it (via the existing `GetLoadedSolution()` block) so the task reports `Succeeded` only once the solution is genuinely query-ready. No accessor needs a new "still loading" code path.

## Concurrency / edge cases

| Case | Behaviour |
|---|---|
| Bad `path` | Synchronous `FileNotFoundException` before any task starts (both modes) |
| Filter matches no project | `LoadSolutionAsync` throws `McpToolException(InvalidArgument)`; store records it as `Failed` with code/message |
| Same path already loaded, no new filter | `LoadSolutionAsync` re-activates instantly; task succeeds immediately |
| Two concurrent background loads of the same path | `LoadSolutionAsync`'s existing double-checked locking dedups; one wins, the other re-activates |
| Task eviction | Inherited from `BackgroundTaskStore` (60-min terminal eviction); same as `rebuild_solution` |

## Testing

xUnit, against the `TestSolution` fixture, mirroring `StartBackgroundTaskToolTests`.

- `Execute_Background_ReturnsRunningTaskThatSucceeds` — `background: true` returns a `BackgroundTaskInfo`; polling `get_task_status` reaches `Succeeded` with a non-null result.
- `Execute_Background_BadPath_ThrowsSynchronously` — validation happens before the task is queued.
- `Execute_Background_DoesNotBlockReturn` — the call returns while `Status == Running` (the whole point).
- `Execute_Sync_StillReturnsString` — regression gate on the default path (existing tests, updated for the `object` return).

## Out of scope

- Cancelling an in-flight load (`BackgroundTaskStore` has a CTS, but `LoadSolutionAsync` doesn't thread a token through `OpenAsync` yet — a separate change, matching `rebuild_solution`'s current limitation).
- A dedicated `get_load_status` tool — `get_task_status` covers it.
- Per-phase progress (`opening` vs `compiling`) in the status — the binary Running/Succeeded is enough to unblock the agent; revisit if asked.
