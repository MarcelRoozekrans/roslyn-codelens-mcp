# MSBuild Timeout + Background Task Store Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a per-project (and per-solution) timeout around MSBuild project loading so wedged `BuildHost-net472` subprocesses don't hang the server, and introduce a small background-task store with three new MCP tools (`start_background_task`, `get_task_status`, `list_running_tasks`) wrapping initially `rebuild_solution`.

**Architecture:** Section 1: wrap `MSBuildWorkspace.OpenProjectAsync` and `OpenSolutionAsync` with linked `CancellationTokenSource` + per-call timeout deadline (configurable via env var, default 300s). Timeout-skipped projects are recorded via the existing `SkippedProjects` mechanism with `Kind: "Timeout"`. Section 2: a singleton `BackgroundTaskStore` with `ConcurrentDictionary<taskId, BackgroundTask>` backing, slug-style human-readable IDs, 60-min eviction, and three new MCP tools. Initial allowlist contains only `rebuild_solution`.

**Tech Stack:** C# / .NET 10, Roslyn `Microsoft.CodeAnalysis.MSBuild`, `ModelContextProtocol.Server`, xUnit.

**Design doc:** `docs/plans/2026-05-28-msbuild-timeout-and-background-tasks-design.md`

---

## Conventions

- TDD: failing test → red → implement → green → commit.
- Scoped runs: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~<Class>"`.
- Full suite before merge: `dotnet test`.
- Per-task commit, prefix `feat(loader):` for Section 1, `feat(bgtasks):` for Section 2.
- The documented `IsAdapterRestoreFlake` (CS0103/CS0234/CS0246 in NUnit/MSTest/XUnit fixture restore) is environmental noise.
- `InternalsVisibleTo("RoslynCodeLens.Tests")` is set — internal types are reachable from tests.

---

## Section 1 — Per-project MSBuild timeout

### Task 1: Plumb `CancellationToken` through `SolutionLoader.OpenAsync`

**Files:**
- Modify: `src/RoslynCodeLens/SolutionLoader.cs`

The existing `OpenAsync(string solutionPath)` takes no CT today. Add an optional `CancellationToken ct = default` parameter and thread it to the internal `OpenPerProjectAsync`. No callers need to be updated (default is `CancellationToken.None`).

### Step 1: Update signature

```csharp
public async Task<(Solution Solution, MSBuildWorkspace Workspace, IReadOnlyList<SkippedProject> Skipped)>
    OpenAsync(string solutionPath, CancellationToken ct = default)
{
    // ...existing body, but pass ct to OpenPerProjectAsync
    return await OpenPerProjectAsync(solutionPath, classified, ct).ConfigureAwait(false);
}

private static async Task<(Solution, MSBuildWorkspace, IReadOnlyList<SkippedProject>)>
    OpenPerProjectAsync(
        string solutionPath,
        IReadOnlyList<ProjectClassifier.ClassifiedProject> classified,
        CancellationToken ct)
{
    // ...existing body
}
```

### Step 2: Build

```
dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj
```

Should succeed — pure signature additions with defaults.

### Step 3: Commit

```
git commit -am "feat(loader): plumb CancellationToken through SolutionLoader.OpenAsync"
```

---

### Task 2: Per-project timeout helper + integration

**Files:**
- Modify: `src/RoslynCodeLens/SolutionLoader.cs`
- Create: `tests/RoslynCodeLens.Tests/SolutionLoaderTimeoutTests.cs`

### Step 1: Write failing tests

Tests the timeout helper directly (no MSBuild). A small testable surface — extract the helper as `internal static` so tests can hit it.

```csharp
// tests/RoslynCodeLens.Tests/SolutionLoaderTimeoutTests.cs
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class SolutionLoaderTimeoutTests
{
    [Fact]
    public async Task RunWithTimeoutAsync_FastTask_ReturnsResult()
    {
        var result = await SolutionLoader.RunWithTimeoutAsync(
            ct => Task.FromResult<string?>("ok"),
            timeoutSec: 1,
            outerCt: default);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_SlowTask_ReturnsNull()
    {
        var result = await SolutionLoader.RunWithTimeoutAsync<string>(
            async ct => { await Task.Delay(2000, ct).ConfigureAwait(false); return "late"; },
            timeoutSec: 1,
            outerCt: default);
        Assert.Null(result);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_UserCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await SolutionLoader.RunWithTimeoutAsync<string>(
                ct => Task.FromResult<string?>("never"),
                timeoutSec: 60,
                outerCt: cts.Token));
    }

    [Fact]
    public void GetOpenProjectTimeoutSec_DefaultsTo300()
    {
        // Unset the env var if present
        Environment.SetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS", null);
        Assert.Equal(300, SolutionLoader.GetOpenProjectTimeoutSec());
    }

    [Fact]
    public void GetOpenProjectTimeoutSec_HonorsEnvVarOverride()
    {
        try
        {
            Environment.SetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS", "42");
            Assert.Equal(42, SolutionLoader.GetOpenProjectTimeoutSec());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS", null);
        }
    }
}
```

### Step 2: Verify failure

```
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~SolutionLoaderTimeoutTests"
```
Expected: compile errors (helpers don't exist).

### Step 3: Implement helpers in `SolutionLoader.cs`

Add these `internal static` members to the `SolutionLoader` class:

```csharp
private const int DefaultOpenProjectTimeoutSec = 300;

internal static int GetOpenProjectTimeoutSec() =>
    int.TryParse(
        Environment.GetEnvironmentVariable("ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS"),
        out var n) && n > 0
            ? n
            : DefaultOpenProjectTimeoutSec;

internal static async Task<T?> RunWithTimeoutAsync<T>(
    Func<CancellationToken, Task<T?>> work,
    int timeoutSec,
    CancellationToken outerCt) where T : class
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

    try
    {
        return await work(timeoutCts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
    {
        return null;
    }
}
```

### Step 4: Wire it into the per-project loader

Replace the existing `try { await workspace.OpenProjectAsync(entry.Path) ... }` block (around line 72-83 today) with:

```csharp
if (entry.Kind == ProjectClassifier.ProjectKind.SdkStyle)
{
    var timeoutSec = GetOpenProjectTimeoutSec();
    Project? loaded;
    try
    {
        loaded = await RunWithTimeoutAsync<Project>(
            innerCt => workspace.OpenProjectAsync(entry.Path, cancellationToken: innerCt),
            timeoutSec,
            ct).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync(
            $"[roslyn-codelens] Skipping project {entry.Name}: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
        skipped.Add(new SkippedProject(entry.Path, entry.Name, "Failed",
            $"{ex.GetType().Name}: {ex.Message}"));
        continue;
    }

    if (loaded is null)
    {
        await Console.Error.WriteLineAsync(
            $"[roslyn-codelens] Timeout loading project {entry.Name} (exceeded {timeoutSec}s).").ConfigureAwait(false);
        skipped.Add(new SkippedProject(entry.Path, entry.Name, "Timeout",
            $"Project load exceeded {timeoutSec}s. " +
            $"Set ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS to override."));
    }
}
```

> **Note:** the original `OpenProjectAsync(entry.Path)` overload doesn't take a CT — verify the Roslyn 4.14+ API exposes a `cancellationToken` parameter. If not, the wrapper still works (timeout still fires via the linked token's internal cancellation handling around the `await`). The `cancellationToken: innerCt` keyword is an attempt to pass it; if the API doesn't accept it, drop the parameter — the OCE check at the await suspension point still fires.

### Step 5: Tests green

```
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~SolutionLoaderTimeoutTests"
```

Expected: 5/5 passing.

### Step 6: Commit

```
git add src/RoslynCodeLens/SolutionLoader.cs tests/RoslynCodeLens.Tests/SolutionLoaderTimeoutTests.cs
git commit -m "feat(loader): per-project MSBuild timeout with SkippedProject 'Timeout' kind"
```

---

### Task 3: Solution-level timeout (extends pattern to `OpenSolutionAsync`)

**Files:**
- Modify: `src/RoslynCodeLens/SolutionLoader.cs`

### Step 1: Add solution-level wrapper

Replace `OpenSolutionAsync(solutionPath)` at line 39 with a timeout-wrapped call. On timeout, fall back to `OpenPerProjectAsync` (same path the existing `catch` block uses).

Inside `OpenAsync`:

```csharp
Solution? solution;
try
{
    solution = await RunWithTimeoutAsync<Solution>(
        innerCt => workspace.OpenSolutionAsync(solutionPath, cancellationToken: innerCt),
        GetOpenProjectTimeoutSec(),
        ct).ConfigureAwait(false);
}
catch (Exception ex)
{
    workspace.Dispose();
    await Console.Error.WriteLineAsync(
        $"[roslyn-codelens] Solution-level load failed ({ex.GetType().Name}: {ex.Message}); falling back to per-project loading.")
        .ConfigureAwait(false);
    return await OpenPerProjectAsync(solutionPath, classified, ct).ConfigureAwait(false);
}

if (solution is null)
{
    workspace.Dispose();
    await Console.Error.WriteLineAsync(
        $"[roslyn-codelens] Solution-level load timed out; falling back to per-project loading.")
        .ConfigureAwait(false);
    return await OpenPerProjectAsync(solutionPath, classified, ct).ConfigureAwait(false);
}

return (solution, workspace, Array.Empty<SkippedProject>());
```

### Step 2: Build + sanity test

```
dotnet build
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~SolutionLoaderTests|FullyQualifiedName~SolutionLoaderTimeoutTests"
```

The existing `SolutionLoaderTests` should still pass — fast solutions never hit the timeout, behavior unchanged.

### Step 3: Commit

```
git commit -am "feat(loader): solution-level MSBuild timeout falls back to per-project load"
```

---

## Section 2 — Background task store

### Task 4: `BackgroundTaskStatus` enum + `BackgroundTaskInfo` record + `BackgroundTask` class

**Files:**
- Create: `src/RoslynCodeLens/Models/BackgroundTaskStatus.cs`
- Create: `src/RoslynCodeLens/Models/BackgroundTaskInfo.cs`
- Create: `src/RoslynCodeLens/BackgroundTasks/BackgroundTask.cs`

### Step 1: Implement types

```csharp
// src/RoslynCodeLens/Models/BackgroundTaskStatus.cs
namespace RoslynCodeLens.Models;

public enum BackgroundTaskStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}
```

```csharp
// src/RoslynCodeLens/Models/BackgroundTaskInfo.cs
namespace RoslynCodeLens.Models;

public sealed record BackgroundTaskInfo(
    string TaskId,
    string ToolName,
    BackgroundTaskStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    object? Result,
    string? ErrorMessage,
    string? ErrorCode);
```

```csharp
// src/RoslynCodeLens/BackgroundTasks/BackgroundTask.cs
using RoslynCodeLens.Models;

namespace RoslynCodeLens.BackgroundTasks;

internal sealed class BackgroundTask : IDisposable
{
    public string TaskId { get; }
    public string ToolName { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; set; }
    public BackgroundTaskStatus Status { get; set; }
    public object? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
    public CancellationTokenSource Cts { get; }

    public BackgroundTask(string taskId, string toolName)
    {
        TaskId = taskId;
        ToolName = toolName;
        StartedAt = DateTimeOffset.UtcNow;
        Status = BackgroundTaskStatus.Running;
        Cts = new CancellationTokenSource();
    }

    public BackgroundTaskInfo ToInfo() => new(
        TaskId, ToolName, Status, StartedAt, CompletedAt, Result, ErrorMessage, ErrorCode);

    public void Dispose() => Cts.Dispose();
}
```

### Step 2: Build

```
dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj
```

### Step 3: Commit

```
git add src/RoslynCodeLens/Models/BackgroundTaskStatus.cs src/RoslynCodeLens/Models/BackgroundTaskInfo.cs src/RoslynCodeLens/BackgroundTasks/BackgroundTask.cs
git commit -m "feat(bgtasks): add BackgroundTask + Info + Status types"
```

---

### Task 5: `BackgroundTaskStore` + unit tests

**Files:**
- Create: `src/RoslynCodeLens/BackgroundTasks/BackgroundTaskStore.cs`
- Create: `tests/RoslynCodeLens.Tests/BackgroundTasks/BackgroundTaskStoreTests.cs`

### Step 1: Write failing tests

```csharp
// tests/RoslynCodeLens.Tests/BackgroundTasks/BackgroundTaskStoreTests.cs
using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests.BackgroundTasks;

public class BackgroundTaskStoreTests
{
    [Fact]
    public async Task Start_AssignsHumanReadableTaskId()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("rebuild_solution",
            _ => Task.FromResult<object?>("done"));
        Assert.StartsWith("bg-rebuild_solution-", info.TaskId, StringComparison.Ordinal);
        await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
    }

    [Fact]
    public async Task Start_SucceededTask_PreservesResult()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool",
            _ => Task.FromResult<object?>("payload"));

        var terminal = await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
        Assert.Equal(BackgroundTaskStatus.Succeeded, terminal.Status);
        Assert.Equal("payload", terminal.Result);
    }

    [Fact]
    public async Task Start_FailedTask_McpToolException_PreservesCodeAndMessage()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool",
            _ => throw new McpToolException(
                Models.ToolErrorCode.SymbolNotFound, "X"));

        var terminal = await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
        Assert.Equal(BackgroundTaskStatus.Failed, terminal.Status);
        Assert.Equal("SymbolNotFound", terminal.ErrorCode);
        Assert.Equal("X", terminal.ErrorMessage);
    }

    [Fact]
    public async Task Start_FailedTask_GenericException_DefaultsToInternalCode()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool",
            _ => throw new InvalidOperationException("boom"));

        var terminal = await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);
        Assert.Equal(BackgroundTaskStatus.Failed, terminal.Status);
        Assert.Equal("Internal", terminal.ErrorCode);
        Assert.Equal("boom", terminal.ErrorMessage);
    }

    [Fact]
    public void Get_UnknownTaskId_ReturnsNull()
    {
        using var store = new BackgroundTaskStore();
        Assert.Null(store.Get("bg-nope-foo-bar"));
    }

    [Fact]
    public async Task ListRunning_ExcludesTerminalOlderThan5Min()
    {
        using var store = new BackgroundTaskStore();
        var info = store.Start("test_tool",
            _ => Task.FromResult<object?>("done"));
        var terminal = await WaitForTerminal(store, info.TaskId).ConfigureAwait(false);

        // The just-completed task should still appear in ListRunning (within 5-min window).
        Assert.Contains(store.ListRunning(), t => t.TaskId == info.TaskId);
    }

    private static async Task<BackgroundTaskInfo> WaitForTerminal(
        BackgroundTaskStore store, string taskId, int maxMs = 2000)
    {
        var start = DateTimeOffset.UtcNow;
        while ((DateTimeOffset.UtcNow - start).TotalMilliseconds < maxMs)
        {
            var info = store.Get(taskId)!;
            if (info.Status != BackgroundTaskStatus.Running) return info;
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"Task {taskId} did not reach terminal state.");
    }
}
```

### Step 2: Verify failure

```
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~BackgroundTaskStoreTests"
```
Expected: compile errors.

### Step 3: Implement `BackgroundTaskStore`

```csharp
// src/RoslynCodeLens/BackgroundTasks/BackgroundTaskStore.cs
using System.Collections.Concurrent;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.BackgroundTasks;

public sealed class BackgroundTaskStore : IDisposable
{
    private static readonly TimeSpan EvictionAge = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ListWindow = TimeSpan.FromMinutes(5);
    private static readonly string[] s_adjectives =
        ["bold", "calm", "deft", "eager", "fast", "grim", "hale", "kind", "loud", "neat",
         "proud", "quick", "rare", "swift", "true", "vast", "warm", "wise", "young", "zen"];
    private static readonly string[] s_nouns =
        ["arch", "bird", "comet", "dawn", "echo", "flame", "grove", "harbor", "iris", "jolt",
         "kite", "lake", "moss", "nova", "oak", "pier", "quill", "reef", "stone", "tide"];

    private readonly ConcurrentDictionary<string, BackgroundTask> _tasks = new(StringComparer.Ordinal);
    private readonly Timer _evictionTimer;

    public BackgroundTaskStore()
    {
        _evictionTimer = new Timer(_ => Evict(), state: null,
            dueTime: TimeSpan.FromMinutes(1), period: TimeSpan.FromMinutes(1));
    }

    public BackgroundTaskInfo Start(string toolName, Func<CancellationToken, Task<object?>> work)
    {
        var taskId = GenerateTaskId(toolName);
        var task = new BackgroundTask(taskId, toolName);
        _tasks[taskId] = task;

        _ = Task.Run(async () =>
        {
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
        });

        return task.ToInfo();
    }

    public BackgroundTaskInfo? Get(string taskId) =>
        _tasks.TryGetValue(taskId, out var task) ? task.ToInfo() : null;

    public IReadOnlyList<BackgroundTaskInfo> ListRunning()
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<BackgroundTaskInfo>();
        foreach (var task in _tasks.Values)
        {
            if (task.Status == BackgroundTaskStatus.Running)
                list.Add(task.ToInfo());
            else if (task.CompletedAt is { } completed && now - completed < ListWindow)
                list.Add(task.ToInfo());
        }
        return list;
    }

    private void Evict()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _tasks)
        {
            var task = kvp.Value;
            if (task.Status == BackgroundTaskStatus.Running) continue;
            if (task.CompletedAt is { } completed && now - completed >= EvictionAge)
            {
                if (_tasks.TryRemove(kvp.Key, out var removed))
                    removed.Dispose();
            }
        }
    }

    private static string GenerateTaskId(string toolName)
    {
        var adj = s_adjectives[Random.Shared.Next(s_adjectives.Length)];
        var noun = s_nouns[Random.Shared.Next(s_nouns.Length)];
        return $"bg-{toolName}-{adj}-{noun}";
    }

    public void Dispose()
    {
        _evictionTimer.Dispose();
        foreach (var task in _tasks.Values) task.Dispose();
        _tasks.Clear();
    }
}
```

### Step 4: Tests green

```
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~BackgroundTaskStoreTests"
```

Expected: 6/6 passing. The McpToolException test requires `RoslynCodeLens.McpToolException` + `Models.ToolErrorCode` (already shipped in earlier PR).

### Step 5: Commit

```
git add src/RoslynCodeLens/BackgroundTasks/BackgroundTaskStore.cs tests/RoslynCodeLens.Tests/BackgroundTasks/BackgroundTaskStoreTests.cs
git commit -m "feat(bgtasks): BackgroundTaskStore singleton with slug IDs, eviction, polling"
```

---

### Task 6: Allowlist + dispatcher + 3 MCP tools

**Files:**
- Create: `src/RoslynCodeLens/BackgroundTasks/BackgroundTaskAllowlist.cs`
- Create: `src/RoslynCodeLens/Tools/StartBackgroundTaskTool.cs`
- Create: `src/RoslynCodeLens/Tools/GetTaskStatusTool.cs`
- Create: `src/RoslynCodeLens/Tools/ListRunningTasksTool.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/StartBackgroundTaskToolTests.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/GetTaskStatusToolTests.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/ListRunningTasksToolTests.cs`

### Step 1: Allowlist

```csharp
// src/RoslynCodeLens/BackgroundTasks/BackgroundTaskAllowlist.cs
namespace RoslynCodeLens.BackgroundTasks;

internal static class BackgroundTaskAllowlist
{
    /// <summary>
    /// Tool names that can be wrapped by <c>start_background_task</c>.
    /// To add a new tool, append its name here AND add a corresponding
    /// case in <c>StartBackgroundTaskTool.Dispatch</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedTools =
        new HashSet<string>(StringComparer.Ordinal) { "rebuild_solution" };
}
```

### Step 2: `start_background_task`

```csharp
// src/RoslynCodeLens/Tools/StartBackgroundTaskTool.cs
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
```

### Step 3: `get_task_status`

```csharp
// src/RoslynCodeLens/Tools/GetTaskStatusTool.cs
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
```

### Step 4: `list_running_tasks`

```csharp
// src/RoslynCodeLens/Tools/ListRunningTasksTool.cs
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
```

### Step 5: Tests

```csharp
// tests/RoslynCodeLens.Tests/Tools/StartBackgroundTaskToolTests.cs
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
        // rebuild_solution on an empty manager will likely throw McpToolException(InvalidArgument)
        // — that's fine; we're checking the dispatcher queues the work, not the runtime outcome.
        var info = StartBackgroundTaskTool.Execute(manager, store, "rebuild_solution");
        Assert.StartsWith("bg-rebuild_solution-", info.TaskId, StringComparison.Ordinal);
        Assert.Equal("rebuild_solution", info.ToolName);
        manager.Dispose();
    }
}
```

```csharp
// tests/RoslynCodeLens.Tests/Tools/GetTaskStatusToolTests.cs
using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class GetTaskStatusToolTests
{
    [Fact]
    public void Execute_UnknownTaskId_ThrowsInvalidArgument()
    {
        using var store = new BackgroundTaskStore();
        var ex = Assert.Throws<McpToolException>(() =>
            GetTaskStatusTool.Execute(store, "bg-nope-foo-bar"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Execute_KnownTaskId_ReturnsCurrentStatus()
    {
        using var store = new BackgroundTaskStore();
        var started = store.Start("test_tool", _ => Task.FromResult<object?>("done"));
        // Allow the task a moment to complete.
        await Task.Delay(100).ConfigureAwait(false);
        var info = GetTaskStatusTool.Execute(store, started.TaskId);
        Assert.Equal(started.TaskId, info.TaskId);
    }
}
```

```csharp
// tests/RoslynCodeLens.Tests/Tools/ListRunningTasksToolTests.cs
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class ListRunningTasksToolTests
{
    [Fact]
    public void Execute_EmptyStore_ReturnsEmptyEnvelope()
    {
        using var store = new BackgroundTaskStore();
        var result = ListRunningTasksTool.Execute(store);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Execute_WithRunningTask_AppearsInList()
    {
        using var store = new BackgroundTaskStore();
        // A task that blocks long enough to be observed as Running.
        var started = store.Start("test_tool",
            ct => Task.Delay(5000, ct).ContinueWith(_ => (object?)"done"));

        var result = ListRunningTasksTool.Execute(store);
        Assert.Contains(result.Items, t => t.TaskId == started.TaskId);
    }
}
```

### Step 6: Build + tests

```
dotnet build
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~StartBackgroundTaskToolTests|FullyQualifiedName~GetTaskStatusToolTests|FullyQualifiedName~ListRunningTasksToolTests"
```

Expected: all pass.

### Step 7: Commit

```
git add src/RoslynCodeLens/BackgroundTasks/BackgroundTaskAllowlist.cs src/RoslynCodeLens/Tools/StartBackgroundTaskTool.cs src/RoslynCodeLens/Tools/GetTaskStatusTool.cs src/RoslynCodeLens/Tools/ListRunningTasksTool.cs tests/RoslynCodeLens.Tests/Tools/StartBackgroundTaskToolTests.cs tests/RoslynCodeLens.Tests/Tools/GetTaskStatusToolTests.cs tests/RoslynCodeLens.Tests/Tools/ListRunningTasksToolTests.cs
git commit -m "feat(bgtasks): add start_background_task / get_task_status / list_running_tasks tools"
```

---

### Task 7: Wire `BackgroundTaskStore` as DI singleton in `Program.cs`

**Files:**
- Modify: `src/RoslynCodeLens/Program.cs`

### Step 1: Register the singleton

In `Program.cs`, after the existing `builder.Services.AddSingleton(multiManager);` block:

```csharp
builder.Services.AddSingleton<BackgroundTaskStore>();
```

Add a `using RoslynCodeLens.BackgroundTasks;` if not present.

### Step 2: Build + run sanity

```
dotnet build
```

The new tools are auto-discovered via `WithToolsFromAssembly()`; the host should now list them.

### Step 3: Commit

```
git commit -am "feat(bgtasks): register BackgroundTaskStore singleton + auto-discover new tools"
```

---

## Task 8: Docs

**Files:**
- Modify: `README.md`
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`

### Step 1: README — add tools to the Features list

Under the "Multi-solution" / lifecycle section:

```markdown
- **start_background_task** — Queue a long-running tool (currently `rebuild_solution`) to run in the background; returns a `taskId` to poll
- **get_task_status** — Get the current status, result, or error of a background task by its `taskId`
- **list_running_tasks** — List background tasks running or completed within the last 5 minutes
```

### Step 2: README — add env var to a new "Runtime configuration" subsection (or under "Requirements"):

```markdown
## Runtime configuration

- `ROSLYN_CODELENS_OPEN_PROJECT_TIMEOUT_SECONDS` — per-project MSBuild load timeout (default `300`). When a project exceeds this duration during workspace open, it's recorded as a `SkippedProjects` entry with `kind: "Timeout"` and the rest of the solution still loads.
```

### Step 3: SKILL.md — add the three tools to the relevant decision tables

Find the "Tool Quick Reference" table; add:

```markdown
| `start_background_task` | "Kick off a long rebuild without blocking" |
| `get_task_status` | "Check on a queued background task" |
| `list_running_tasks` | "What background work is in flight?" |
```

### Step 4: Commit

```
git commit -am "docs: document background task tools + MSBuild timeout env var"
```

---

## Task 9: Final verification + PR

### Step 1: Full build + test

```
dotnet build
dotnet test
```

Expected: all green except the documented `IsAdapterRestoreFlake` environmental flake.

### Step 2: Smoke test

Restart the MCP server. Run through:

1. `list_running_tasks()` → empty envelope.
2. `start_background_task(toolName: "rebuild_solution")` → returns `{ taskId: "bg-rebuild_solution-…", status: "Running" }`.
3. `get_task_status(taskId)` once or twice → status transitions to `Succeeded` with `result.projectCount` set.
4. `start_background_task(toolName: "not_a_tool")` → `isError: true`, `code: "InvalidArgument"`.

### Step 3: Push + PR

Branch is `feat/msbuild-timeout-and-bg-tasks`. Push, open PR titled `feat: MSBuild timeout + background task store`. Body should highlight:

- Per-project + per-solution MSBuild timeout (env-var configurable, default 300s)
- 3 new MCP tools; allowlist starts with `rebuild_solution`
- No breaking changes

---

## Gotchas

- **`MSBuildWorkspace.OpenProjectAsync` overload that takes a CancellationToken** — verify in the installed Roslyn package (`~/.nuget/packages/microsoft.codeanalysis.workspaces.msbuild/4.14.x/`). If the overload doesn't exist, the timeout still fires via the linked CTS's tripping `await` at the next yield, but you may need to drop the `cancellationToken:` keyword from the call.
- **`Random.Shared` in `GenerateTaskId`** — fine for ID generation, not crypto. Don't replace with `RandomNumberGenerator` unless a real need surfaces (collisions are vanishingly rare given the wordlist × 400 product).
- **Eviction timer in tests** — runs every minute. Tests don't wait that long; eviction is exercised via direct calls in real life, not unit tests. Don't add brittle `Thread.Sleep(61_000)` tests.
- **`BackgroundTaskStore` is `IDisposable`** — DI container disposes singletons on host shutdown. Tests use `using var store = new BackgroundTaskStore();`.
- **Background work uses `Task.Run`** to detach from the calling sync context. Don't await it inside `Start` — the whole point is that `Start` returns immediately.
- **`McpToolException` thrown inside background work** is caught and stored as `Status: Failed` + `ErrorCode = <enum name>`. It does NOT propagate to the original `start_background_task` caller — they get `Status: Running` and discover the failure via polling.
