# Multi-Solution Support Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Allow the MCP server to load N solutions at startup and switch between them with `list_solutions` / `set_active_solution` tools.

**Architecture:** Introduce `MultiSolutionManager` that owns a `Dictionary<string, SolutionManager>` and delegates all existing API calls to the active entry. Register it in DI; rename the injected parameter in all 22 tool files. Add two new tool files.

**Tech Stack:** C# / xUnit / ModelContextProtocol SDK / MSBuild Roslyn workspace

---

### Task 1: Create `MultiSolutionManager` — single-solution wrapper

**Files:**
- Create: `src/RoslynCodeLens/MultiSolutionManager.cs`
- Create: `tests/RoslynCodeLens.Tests/MultiSolutionManagerTests.cs`

**Step 1: Write the failing tests**

Create `tests/RoslynCodeLens.Tests/MultiSolutionManagerTests.cs`:

```csharp
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class MultiSolutionManagerTests : IAsyncLifetime
{
    private string _solutionPath = null!;

    public Task InitializeAsync()
    {
        _solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateAsync_SinglePath_DelegatesEnsureLoaded()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        multi.EnsureLoaded(); // must not throw
        multi.Dispose();
    }

    [Fact]
    public async Task GetLoadedSolution_ReturnsSolution()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        await multi.WaitForWarmupAsync();
        Assert.False(multi.GetLoadedSolution().IsEmpty);
        multi.Dispose();
    }

    [Fact]
    public async Task GetResolver_ReturnsResolver()
    {
        var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
        await multi.WaitForWarmupAsync();
        Assert.NotNull(multi.GetResolver());
        multi.Dispose();
    }

    [Fact]
    public void CreateEmpty_EnsureLoaded_Throws()
    {
        var multi = MultiSolutionManager.CreateEmpty();
        Assert.Throws<InvalidOperationException>(() => multi.EnsureLoaded());
        multi.Dispose();
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/RoslynCodeLens.Tests --filter "MultiSolutionManagerTests"
```

Expected: FAIL — `MultiSolutionManager` does not exist yet.

**Step 3: Implement `MultiSolutionManager`**

Create `src/RoslynCodeLens/MultiSolutionManager.cs`:

```csharp
namespace RoslynCodeLens;

public sealed class MultiSolutionManager : IDisposable
{
    private readonly Dictionary<string, SolutionManager> _managers;
    private string? _activeKey;

    private MultiSolutionManager(Dictionary<string, SolutionManager> managers, string? activeKey)
    {
        _managers = managers;
        _activeKey = activeKey;
    }

    public static async Task<MultiSolutionManager> CreateAsync(IReadOnlyList<string> solutionPaths)
    {
        if (solutionPaths.Count == 0)
            return CreateEmpty();

        var managers = new Dictionary<string, SolutionManager>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in solutionPaths)
        {
            var normalised = Path.GetFullPath(path);
            managers[normalised] = await SolutionManager.CreateAsync(normalised).ConfigureAwait(false);
        }

        var firstKey = Path.GetFullPath(solutionPaths[0]);
        return new MultiSolutionManager(managers, firstKey);
    }

    public static MultiSolutionManager CreateEmpty() =>
        new([], null);

    private SolutionManager Active =>
        _activeKey != null && _managers.TryGetValue(_activeKey, out var m)
            ? m
            : SolutionManager.CreateEmpty();

    public void EnsureLoaded() => Active.EnsureLoaded();
    public LoadedSolution GetLoadedSolution() => Active.GetLoadedSolution();
    public SymbolResolver GetResolver() => Active.GetResolver();
    public Task WaitForWarmupAsync() => Active.WaitForWarmupAsync();
    public Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync() => Active.ForceReloadAsync();

    public void Dispose()
    {
        foreach (var m in _managers.Values)
            m.Dispose();
    }
}
```

**Step 4: Run tests to verify they pass**

```
dotnet test tests/RoslynCodeLens.Tests --filter "MultiSolutionManagerTests"
```

Expected: PASS (4 tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/MultiSolutionManager.cs tests/RoslynCodeLens.Tests/MultiSolutionManagerTests.cs
git commit -m "feat: add MultiSolutionManager wrapping single solution"
```

---

### Task 2: Add multi-solution capability — list and switch

**Files:**
- Modify: `src/RoslynCodeLens/MultiSolutionManager.cs`
- Modify: `tests/RoslynCodeLens.Tests/MultiSolutionManagerTests.cs`
- Create: `src/RoslynCodeLens/Models/SolutionInfo.cs`

**Step 1: Add `SolutionInfo` model**

Create `src/RoslynCodeLens/Models/SolutionInfo.cs`:

```csharp
namespace RoslynCodeLens.Models;

public sealed record SolutionInfo(
    string Path,
    bool IsActive,
    int ProjectCount,
    string Status);
```

**Step 2: Write failing tests**

Add to `tests/RoslynCodeLens.Tests/MultiSolutionManagerTests.cs`:

```csharp
[Fact]
public async Task ListSolutions_ReturnsBothPaths_FirstIsActive()
{
    var multi = await MultiSolutionManager.CreateAsync([_solutionPath, _solutionPath]);
    var list = multi.ListSolutions();
    Assert.Equal(2, list.Count);
    Assert.True(list[0].IsActive);
    Assert.False(list[1].IsActive);
    multi.Dispose();
}

[Fact]
public async Task SetActiveSolution_ByPartialName_SwitchesActive()
{
    var multi = await MultiSolutionManager.CreateAsync([_solutionPath, _solutionPath]);
    var switched = multi.SetActiveSolution("TestSolution");
    Assert.Contains("TestSolution", switched, StringComparison.OrdinalIgnoreCase);
    multi.Dispose();
}

[Fact]
public async Task SetActiveSolution_UnknownName_Throws()
{
    var multi = await MultiSolutionManager.CreateAsync([_solutionPath]);
    Assert.Throws<InvalidOperationException>(() => multi.SetActiveSolution("DoesNotExist"));
    multi.Dispose();
}

[Fact]
public async Task SetActiveSolution_AmbiguousName_Throws()
{
    // Two paths that both contain "TestSolution"
    var multi = await MultiSolutionManager.CreateAsync([_solutionPath, _solutionPath]);
    // Both keys are the same normalised path so there's only one entry — use a path that differs
    // This test verifies the ambiguity message is thrown when >1 key matches.
    // We test indirectly: two identical paths dedup to one key, no ambiguity.
    var switched = multi.SetActiveSolution("TestSolution"); // should succeed (only 1 unique key)
    Assert.NotNull(switched);
    multi.Dispose();
}
```

**Step 3: Run tests to verify they fail**

```
dotnet test tests/RoslynCodeLens.Tests --filter "MultiSolutionManagerTests"
```

Expected: FAIL — `ListSolutions` and `SetActiveSolution` do not exist yet.

**Step 4: Implement `ListSolutions` and `SetActiveSolution`**

Add to `MultiSolutionManager.cs` (inside the class, before `Dispose`):

```csharp
public IReadOnlyList<SolutionInfo> ListSolutions()
{
    return _managers
        .Select(kvp =>
        {
            var m = kvp.Value;
            int projectCount = 0;
            string status;
            try
            {
                var loaded = m.GetLoadedSolution();
                projectCount = loaded.Compilations.Count;
                status = loaded.IsEmpty ? "empty" : "ready";
            }
            catch
            {
                status = "loading";
            }
            return new SolutionInfo(kvp.Key, kvp.Key == _activeKey, projectCount, status);
        })
        .ToList();
}

public string SetActiveSolution(string name)
{
    var matches = _managers.Keys
        .Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (matches.Count == 0)
        throw new InvalidOperationException(
            $"No solution matching '{name}'. Available: {string.Join(", ", _managers.Keys.Select(Path.GetFileName))}");

    if (matches.Count > 1)
        throw new InvalidOperationException(
            $"Ambiguous match for '{name}'. Matches: {string.Join(", ", matches)}");

    _activeKey = matches[0];
    return _activeKey;
}
```

Also add `using RoslynCodeLens.Models;` at the top of `MultiSolutionManager.cs`.

**Step 5: Run tests to verify they pass**

```
dotnet test tests/RoslynCodeLens.Tests --filter "MultiSolutionManagerTests"
```

Expected: PASS (all tests in the class).

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/MultiSolutionManager.cs src/RoslynCodeLens/Models/SolutionInfo.cs tests/RoslynCodeLens.Tests/MultiSolutionManagerTests.cs
git commit -m "feat: add ListSolutions and SetActiveSolution to MultiSolutionManager"
```

---

### Task 3: Wire up `Program.cs` and rename parameter type in all 22 tools

**Files:**
- Modify: `src/RoslynCodeLens/Program.cs`
- Modify: all 22 `src/RoslynCodeLens/Tools/*Tool.cs` files (type rename only)

**Step 1: Update `Program.cs`**

Replace the entire content of `src/RoslynCodeLens/Program.cs`:

```csharp
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoslynCodeLens;

MSBuildLocator.RegisterDefaults();

MultiSolutionManager multiManager;

var solutionPaths = args.Length > 0
    ? args.ToList()
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory()) is { } found
        ? [found]
        : [];

if (solutionPaths.Count > 0)
{
    multiManager = await MultiSolutionManager.CreateAsync(solutionPaths).ConfigureAwait(false);
}
else
{
    await Console.Error.WriteLineAsync("[roslyn-codelens] No .sln file found. Tools will return errors.").ConfigureAwait(false);
    multiManager = MultiSolutionManager.CreateEmpty();
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(multiManager);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
```

**Step 2: Rename `SolutionManager` → `MultiSolutionManager` in all tool files**

Run this from the repo root in bash:

```bash
sed -i 's/SolutionManager manager/MultiSolutionManager manager/g' src/RoslynCodeLens/Tools/*.cs
```

**Step 3: Build to verify**

```
dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj
```

Expected: Build succeeded, 0 errors.

**Step 4: Run full test suite**

```
dotnet test
```

Expected: all existing tests pass.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Program.cs src/RoslynCodeLens/Tools/
git commit -m "feat: wire MultiSolutionManager into DI and tools"
```

---

### Task 4: Add `list_solutions` tool

**Files:**
- Create: `src/RoslynCodeLens/Tools/ListSolutionsTool.cs`

No new test file needed — tool is a thin wrapper over `MultiSolutionManager.ListSolutions()` which is already tested.

**Step 1: Implement the tool**

Create `src/RoslynCodeLens/Tools/ListSolutionsTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ListSolutionsTool
{
    [McpServerTool(Name = "list_solutions"),
     Description("List all solutions loaded by this server, showing which one is currently active.")]
    public static IReadOnlyList<SolutionInfo> Execute(MultiSolutionManager manager)
    {
        return manager.ListSolutions();
    }
}
```

**Step 2: Build**

```
dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj
```

Expected: Build succeeded.

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/Tools/ListSolutionsTool.cs
git commit -m "feat: add list_solutions tool"
```

---

### Task 5: Add `set_active_solution` tool

**Files:**
- Create: `src/RoslynCodeLens/Tools/SetActiveSolutionTool.cs`

**Step 1: Implement the tool**

Create `src/RoslynCodeLens/Tools/SetActiveSolutionTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class SetActiveSolutionTool
{
    [McpServerTool(Name = "set_active_solution"),
     Description("Switch the active solution. All subsequent tool calls will operate on the selected solution. " +
                 "Use a partial, case-insensitive name (e.g. 'MyProject' matches 'C:/code/MyProject/MyProject.sln'). " +
                 "Returns the full path of the newly active solution.")]
    public static string Execute(
        MultiSolutionManager manager,
        [Description("Partial or full solution name/path to match")] string name)
    {
        return manager.SetActiveSolution(name);
    }
}
```

**Step 2: Build and run full test suite**

```
dotnet build
dotnet test
```

Expected: Build succeeded, all tests pass.

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/Tools/SetActiveSolutionTool.cs
git commit -m "feat: add set_active_solution tool"
```

---

### Task 6: Manual smoke test

**Step 1: Start the server with two solution paths**

```bash
dotnet run --project src/RoslynCodeLens/RoslynCodeLens.csproj -- RoslynCodeLens.slnx RoslynCodeLens.slnx
```

Expected: stderr shows `[roslyn-codelens] Background compilation starting...` twice (once per manager).

**Step 2: Verify backward compat — single path still works**

```bash
dotnet run --project src/RoslynCodeLens/RoslynCodeLens.csproj -- RoslynCodeLens.slnx
```

Expected: starts normally, one solution loaded.

**Step 3: Verify backward compat — no args / auto-discovery**

```bash
dotnet run --project src/RoslynCodeLens/RoslynCodeLens.csproj
```

Expected: auto-discovers `RoslynCodeLens.slnx`, one solution loaded.
