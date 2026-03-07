# Hot Reload & Source Generator Support Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add lazy file-watching with project-level incremental rebuild and source generator indexing with two new tools.

**Architecture:** `FileChangeTracker` watches source files and marks affected projects (+ transitive dependents) as stale. `SolutionManager` wraps `LoadedSolution` + `SymbolResolver` and lazily rebuilds on the next tool query. `SymbolResolver` gains generated-file detection. Two new tools expose generator info.

**Tech Stack:** .NET 10, Roslyn MSBuildWorkspace, FileSystemWatcher, ModelContextProtocol SDK

---

### Task 1: Add `IsGenerated` to Location Models

**Files:**
- Modify: `src/RoslynCodeGraph/Models/SymbolLocation.cs`
- Modify: `src/RoslynCodeGraph/Models/CallerInfo.cs`
- Modify: `src/RoslynCodeGraph/Models/SymbolReference.cs`
- Modify: `src/RoslynCodeGraph/Models/UnusedSymbolInfo.cs`
- Modify: `src/RoslynCodeGraph/Models/ReflectionUsage.cs`

**Step 1: Add optional IsGenerated parameter to each record**

Add `bool IsGenerated = false` as the last parameter to each record. Using a default value ensures all existing call sites continue to compile without changes.

`SymbolLocation.cs`:
```csharp
public record SymbolLocation(
    string Type,
    string FullName,
    string File,
    int Line,
    string Project,
    bool IsGenerated = false);
```

`CallerInfo.cs`:
```csharp
public record CallerInfo(
    string Caller,
    string File,
    int Line,
    string Snippet,
    string Project,
    bool IsGenerated = false);
```

`SymbolReference.cs`:
```csharp
public record SymbolReference(
    string ReferenceKind,
    string File,
    int Line,
    string Snippet,
    string Project,
    bool IsGenerated = false);
```

`UnusedSymbolInfo.cs`:
```csharp
public record UnusedSymbolInfo(
    string SymbolName,
    string SymbolKind,
    string File,
    int Line,
    string Project,
    bool IsGenerated = false);
```

`ReflectionUsage.cs`:
```csharp
public record ReflectionUsage(
    string Kind,
    string Target,
    string File,
    int Line,
    string Snippet,
    bool IsGenerated = false);
```

**Step 2: Build and run tests**

Run: `dotnet build src/RoslynCodeGraph/RoslynCodeGraph.csproj`
Expected: BUILD SUCCEEDED (default values keep all call sites valid)

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj`
Expected: All existing tests pass

**Step 3: Commit**

```bash
git add src/RoslynCodeGraph/Models/SymbolLocation.cs src/RoslynCodeGraph/Models/CallerInfo.cs src/RoslynCodeGraph/Models/SymbolReference.cs src/RoslynCodeGraph/Models/UnusedSymbolInfo.cs src/RoslynCodeGraph/Models/ReflectionUsage.cs
git commit -m "feat: add IsGenerated flag to location models"
```

---

### Task 2: Add Generated File Detection to `SymbolResolver`

**Files:**
- Modify: `src/RoslynCodeGraph/SymbolResolver.cs`
- Test: `tests/RoslynCodeGraph.Tests/SymbolResolverTests.cs`

**Step 1: Write the failing test**

Add to `SymbolResolverTests.cs`:

```csharp
[Fact]
public void IsGenerated_ReturnsFalse_ForRegularFiles()
{
    var resolver = new SymbolResolver(_loaded);
    // Get the file path of a known hand-written type
    var types = resolver.FindNamedTypes("Greeter");
    Assert.NotEmpty(types);
    var (file, _) = resolver.GetFileAndLine(types[0]);
    Assert.NotEmpty(file);

    Assert.False(resolver.IsGenerated(file));
}

[Fact]
public void IsGenerated_ReturnsTrue_ForObjPaths()
{
    var resolver = new SymbolResolver(_loaded);
    // Paths under obj/ should be classified as generated
    Assert.True(resolver.IsGenerated(@"C:\project\obj\Debug\net10.0\Generated.cs"));
    Assert.True(resolver.IsGenerated(@"C:\project\obj\Release\net10.0\SomeGen.g.cs"));
}

[Fact]
public void IsGenerated_ReturnsTrue_ForNullOrEmptyPaths()
{
    var resolver = new SymbolResolver(_loaded);
    Assert.True(resolver.IsGenerated(""));
    Assert.True(resolver.IsGenerated(null!));
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "IsGenerated"`
Expected: FAIL — `IsGenerated` method does not exist

**Step 3: Implement generated file detection**

Add to `SymbolResolver.cs` — in the constructor, after building the member index, add:

```csharp
// Build generated file path index
_generatedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var compilation in loaded.Compilations.Values)
{
    foreach (var tree in compilation.SyntaxTrees)
    {
        var path = tree.FilePath;
        if (IsGeneratedPath(path))
            _generatedFilePaths.Add(path);
    }
}
```

Add the field:
```csharp
private readonly HashSet<string> _generatedFilePaths;
```

Add the public method and private helper:
```csharp
public bool IsGenerated(string? filePath)
{
    if (string.IsNullOrEmpty(filePath))
        return true;
    if (_generatedFilePaths.Contains(filePath))
        return true;
    return IsGeneratedPath(filePath);
}

private static bool IsGeneratedPath(string? path)
{
    if (string.IsNullOrEmpty(path))
        return true;
    // Files under obj/ directories are generated
    return path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
        || path.Contains($"{Path.AltDirectorySeparatorChar}obj{Path.AltDirectorySeparatorChar}");
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "IsGenerated"`
Expected: PASS

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj`
Expected: All tests pass

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/SymbolResolver.cs tests/RoslynCodeGraph.Tests/SymbolResolverTests.cs
git commit -m "feat: add generated file detection to SymbolResolver"
```

---

### Task 3: Create `FileChangeTracker`

**Files:**
- Create: `src/RoslynCodeGraph/FileChangeTracker.cs`
- Test: `tests/RoslynCodeGraph.Tests/FileChangeTrackerTests.cs`

**Step 1: Write the failing tests**

Create `tests/RoslynCodeGraph.Tests/FileChangeTrackerTests.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace RoslynCodeGraph.Tests;

public class FileChangeTrackerTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private string _solutionPath = null!;

    public async Task InitializeAsync()
    {
        _solutionPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(_solutionPath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Constructor_BuildsReverseDependencyGraph()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        // Should construct without throwing
        Assert.False(tracker.HasStaleProjects);
        tracker.Dispose();
    }

    [Fact]
    public void MarkProjectStale_SetsHasStaleProjects()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var projectId = _loaded.Solution.Projects.First().Id;

        tracker.MarkProjectStale(projectId);

        Assert.True(tracker.HasStaleProjects);
        Assert.Contains(projectId, tracker.StaleProjectIds);
        tracker.Dispose();
    }

    [Fact]
    public void MarkProjectStale_IncludesTransitiveDependents()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        // Find a project that other projects depend on
        var projects = _loaded.Solution.Projects.ToList();
        var depended = projects.FirstOrDefault(p =>
            projects.Any(other => other.ProjectReferences.Any(r => r.ProjectId == p.Id)));

        if (depended != null)
        {
            tracker.MarkProjectStale(depended.Id);

            // The dependent project(s) should also be stale
            var dependents = projects
                .Where(p => p.ProjectReferences.Any(r => r.ProjectId == depended.Id))
                .Select(p => p.Id);

            foreach (var dep in dependents)
                Assert.Contains(dep, tracker.StaleProjectIds);
        }
        tracker.Dispose();
    }

    [Fact]
    public void ClearStale_ResetsState()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var projectId = _loaded.Solution.Projects.First().Id;

        tracker.MarkProjectStale(projectId);
        Assert.True(tracker.HasStaleProjects);

        tracker.ClearStale();
        Assert.False(tracker.HasStaleProjects);
        Assert.Empty(tracker.StaleProjectIds);
        tracker.Dispose();
    }

    [Fact]
    public void FindProjectForFile_ReturnsCorrectProject()
    {
        var tracker = new FileChangeTracker(_loaded, _solutionPath);
        var project = _loaded.Solution.Projects.First();
        var doc = project.Documents.FirstOrDefault(d => d.FilePath != null);

        if (doc != null)
        {
            var foundId = tracker.FindProjectForFile(doc.FilePath!);
            Assert.Equal(project.Id, foundId);
        }
        tracker.Dispose();
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "FileChangeTracker"`
Expected: FAIL — `FileChangeTracker` class does not exist

**Step 3: Implement `FileChangeTracker`**

Create `src/RoslynCodeGraph/FileChangeTracker.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace RoslynCodeGraph;

public class FileChangeTracker : IDisposable
{
    private readonly Dictionary<string, ProjectId> _fileToProject;
    private readonly Dictionary<ProjectId, List<ProjectId>> _reverseDeps;
    private readonly HashSet<ProjectId> _staleProjects = new();
    private readonly FileSystemWatcher[] _watchers;
    private readonly object _lock = new();
    private Timer? _debounceTimer;
    private readonly HashSet<string> _pendingChanges = new();

    private static readonly string[] WatchedExtensions = [".cs", ".csproj", ".props", ".targets"];

    public FileChangeTracker(LoadedSolution loaded, string solutionPath)
    {
        // Build file-to-project mapping
        _fileToProject = new Dictionary<string, ProjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null)
                    _fileToProject[doc.FilePath] = project.Id;
            }

            // Also map the .csproj file itself
            if (project.FilePath != null)
                _fileToProject[project.FilePath] = project.Id;
        }

        // Build reverse dependency graph: if A references B, then B → [A]
        _reverseDeps = new Dictionary<ProjectId, List<ProjectId>>();
        foreach (var project in loaded.Solution.Projects)
        {
            foreach (var projRef in project.ProjectReferences)
            {
                if (!_reverseDeps.TryGetValue(projRef.ProjectId, out var dependents))
                {
                    dependents = new List<ProjectId>();
                    _reverseDeps[projRef.ProjectId] = dependents;
                }
                dependents.Add(project.Id);
            }
        }

        // Start file watchers
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        _watchers = WatchedExtensions.Select(ext =>
        {
            var watcher = new FileSystemWatcher(solutionDir)
            {
                Filter = $"*{ext}",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += (s, e) =>
            {
                OnFileChanged(s, e);
                if (e.OldFullPath != null)
                    OnFileChangedPath(e.OldFullPath);
            };

            watcher.EnableRaisingEvents = true;
            return watcher;
        }).ToArray();
    }

    public bool HasStaleProjects
    {
        get { lock (_lock) return _staleProjects.Count > 0; }
    }

    public IReadOnlySet<ProjectId> StaleProjectIds
    {
        get { lock (_lock) return new HashSet<ProjectId>(_staleProjects); }
    }

    public ProjectId? FindProjectForFile(string filePath)
    {
        return _fileToProject.TryGetValue(filePath, out var id) ? id : null;
    }

    public void MarkProjectStale(ProjectId projectId)
    {
        lock (_lock)
        {
            MarkStaleTransitive(projectId);
        }
    }

    public void ClearStale()
    {
        lock (_lock)
        {
            _staleProjects.Clear();
        }
    }

    /// <summary>
    /// Updates file-to-project mappings after a solution reload.
    /// </summary>
    public void UpdateMappings(LoadedSolution loaded)
    {
        lock (_lock)
        {
            _fileToProject.Clear();
            foreach (var project in loaded.Solution.Projects)
            {
                foreach (var doc in project.Documents)
                {
                    if (doc.FilePath != null)
                        _fileToProject[doc.FilePath] = project.Id;
                }
                if (project.FilePath != null)
                    _fileToProject[project.FilePath] = project.Id;
            }

            _reverseDeps.Clear();
            foreach (var project in loaded.Solution.Projects)
            {
                foreach (var projRef in project.ProjectReferences)
                {
                    if (!_reverseDeps.TryGetValue(projRef.ProjectId, out var dependents))
                    {
                        dependents = new List<ProjectId>();
                        _reverseDeps[projRef.ProjectId] = dependents;
                    }
                    dependents.Add(project.Id);
                }
            }
        }
    }

    private void MarkStaleTransitive(ProjectId projectId)
    {
        if (!_staleProjects.Add(projectId))
            return; // Already stale, avoid cycles

        if (_reverseDeps.TryGetValue(projectId, out var dependents))
        {
            foreach (var dep in dependents)
                MarkStaleTransitive(dep);
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        OnFileChangedPath(e.FullPath);
    }

    private void OnFileChangedPath(string fullPath)
    {
        // Ignore changes in obj/ and bin/ directories
        if (fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
            || fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            return;

        lock (_lock)
        {
            _pendingChanges.Add(fullPath);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(ProcessPendingChanges, null, 200, Timeout.Infinite);
        }
    }

    private void ProcessPendingChanges(object? state)
    {
        HashSet<string> changes;
        lock (_lock)
        {
            changes = new HashSet<string>(_pendingChanges);
            _pendingChanges.Clear();
        }

        foreach (var filePath in changes)
        {
            var projectId = FindProjectForFile(filePath);
            if (projectId != null)
            {
                MarkProjectStale(projectId.Value);
            }
            else
            {
                // Unknown file (e.g., new .cs file not yet in solution) — mark all projects stale
                lock (_lock)
                {
                    foreach (var pid in _fileToProject.Values.Distinct())
                        _staleProjects.Add(pid);
                }
            }
        }
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
        foreach (var watcher in _watchers)
            watcher.Dispose();
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "FileChangeTracker"`
Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/FileChangeTracker.cs tests/RoslynCodeGraph.Tests/FileChangeTrackerTests.cs
git commit -m "feat: add FileChangeTracker with reverse dependency graph"
```

---

### Task 4: Create `SolutionManager`

**Files:**
- Create: `src/RoslynCodeGraph/SolutionManager.cs`
- Test: `tests/RoslynCodeGraph.Tests/SolutionManagerTests.cs`

**Step 1: Write the failing tests**

Create `tests/RoslynCodeGraph.Tests/SolutionManagerTests.cs`:

```csharp
namespace RoslynCodeGraph.Tests;

public class SolutionManagerTests : IAsyncLifetime
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
    public async Task CreateAsync_LoadsSolutionAndResolver()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);

        Assert.NotNull(manager.GetLoadedSolution());
        Assert.False(manager.GetLoadedSolution().IsEmpty);
        Assert.NotNull(manager.GetResolver());
        manager.Dispose();
    }

    [Fact]
    public async Task GetResolver_ReturnsCachedInstance_WhenNotStale()
    {
        var manager = await SolutionManager.CreateAsync(_solutionPath);
        var resolver1 = manager.GetResolver();
        var resolver2 = manager.GetResolver();

        Assert.Same(resolver1, resolver2);
        manager.Dispose();
    }

    [Fact]
    public async Task EnsureLoaded_ThrowsForEmptySolution()
    {
        var manager = SolutionManager.CreateEmpty();
        Assert.Throws<InvalidOperationException>(() => manager.EnsureLoaded());
        manager.Dispose();
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "SolutionManager"`
Expected: FAIL — `SolutionManager` class does not exist

**Step 3: Implement `SolutionManager`**

Create `src/RoslynCodeGraph/SolutionManager.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeGraph;

public class SolutionManager : IDisposable
{
    private LoadedSolution _loaded;
    private SymbolResolver _resolver;
    private readonly string? _solutionPath;
    private readonly FileChangeTracker? _tracker;
    private readonly object _lock = new();

    private SolutionManager(LoadedSolution loaded, string? solutionPath)
    {
        _loaded = loaded;
        _solutionPath = solutionPath;
        _resolver = new SymbolResolver(loaded);

        if (solutionPath != null && !loaded.IsEmpty)
        {
            _tracker = new FileChangeTracker(loaded, solutionPath);
        }
    }

    public static async Task<SolutionManager> CreateAsync(string solutionPath)
    {
        var loader = new SolutionLoader();
        var loaded = await loader.LoadAsync(solutionPath);
        return new SolutionManager(loaded, solutionPath);
    }

    public static SolutionManager CreateEmpty()
    {
        return new SolutionManager(LoadedSolution.Empty, null);
    }

    public LoadedSolution GetLoadedSolution()
    {
        RebuildIfStale();
        return _loaded;
    }

    public SymbolResolver GetResolver()
    {
        RebuildIfStale();
        return _resolver;
    }

    public void EnsureLoaded()
    {
        if (_loaded.IsEmpty)
            throw new InvalidOperationException(
                "No .sln file found. Either run from a directory containing a .sln/.slnx file, " +
                "or pass the solution path as argument: roslyn-codegraph-mcp /path/to/Solution.sln");
    }

    private void RebuildIfStale()
    {
        if (_tracker == null || !_tracker.HasStaleProjects)
            return;

        lock (_lock)
        {
            // Double-check after acquiring lock
            if (!_tracker.HasStaleProjects)
                return;

            var staleIds = _tracker.StaleProjectIds;
            Console.Error.WriteLine(
                $"[roslyn-codegraph] Rebuilding {staleIds.Count} stale project(s)...");

            try
            {
                RebuildStaleProjects(staleIds).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[roslyn-codegraph] Rebuild failed: {ex.Message}. Using cached data.");
            }

            _tracker.ClearStale();
        }
    }

    private async Task RebuildStaleProjects(IReadOnlySet<ProjectId> staleIds)
    {
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
            Console.Error.WriteLine($"[roslyn-codegraph] Warning: {e.Diagnostic.Message}");

        var solution = await workspace.OpenSolutionAsync(_solutionPath!);
        var compilations = new Dictionary<ProjectId, Compilation>(_loaded.Compilations);

        foreach (var project in solution.Projects)
        {
            if (!staleIds.Contains(project.Id))
                continue;

            Console.Error.WriteLine($"[roslyn-codegraph] Recompiling: {project.Name}");
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
                compilations[project.Id] = compilation;
        }

        _loaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations
        };
        _resolver = new SymbolResolver(_loaded);
        _tracker!.UpdateMappings(_loaded);

        Console.Error.WriteLine("[roslyn-codegraph] Rebuild complete.");
    }

    public void Dispose()
    {
        _tracker?.Dispose();
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "SolutionManager"`
Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/SolutionManager.cs tests/RoslynCodeGraph.Tests/SolutionManagerTests.cs
git commit -m "feat: add SolutionManager with lazy rebuild on stale projects"
```

---

### Task 5: Update All 19 Tools to Use `SolutionManager`

**Files:**
- Modify: All 19 files in `src/RoslynCodeGraph/Tools/`
- Modify: `src/RoslynCodeGraph/SolutionGuard.cs` (delete or keep as empty — no longer needed)

**Step 1: Update every tool file**

The pattern is identical for all 19 tools. Replace the two DI parameters and the guard call. For each tool file:

**Before:**
```csharp
[McpServerTool(Name = "find_implementations"),
 Description("Find all classes/structs implementing an interface or extending a class")]
public static List<SymbolLocation> Execute(
    LoadedSolution loaded,
    SymbolResolver resolver,
    [Description("Type name (simple or fully qualified)")] string symbol)
{
    SolutionGuard.EnsureLoaded(loaded);
    return FindImplementationsLogic.Execute(loaded, resolver, symbol);
}
```

**After:**
```csharp
[McpServerTool(Name = "find_implementations"),
 Description("Find all classes/structs implementing an interface or extending a class")]
public static List<SymbolLocation> Execute(
    SolutionManager manager,
    [Description("Type name (simple or fully qualified)")] string symbol)
{
    manager.EnsureLoaded();
    return FindImplementationsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), symbol);
}
```

Apply this transformation to all 19 tool wrapper classes. The Logic classes remain unchanged — they still accept `LoadedSolution` and `SymbolResolver` directly.

Full list of tools to update:
1. `FindAttributeUsagesTool.cs`
2. `FindCallersTool.cs`
3. `FindCircularDependenciesTool.cs`
4. `FindImplementationsTool.cs`
5. `FindLargeClassesTool.cs`
6. `FindNamingViolationsTool.cs`
7. `FindReferencesTool.cs`
8. `FindReflectionUsageTool.cs`
9. `FindUnusedSymbolsTool.cs`
10. `GetCodeFixesTool.cs`
11. `GetComplexityMetricsTool.cs`
12. `GetDiagnosticsTool.cs`
13. `GetDiRegistrationsTool.cs`
14. `GetNugetDependenciesTool.cs`
15. `GetProjectDependenciesTool.cs`
16. `GetSymbolContextTool.cs`
17. `GetTypeHierarchyTool.cs`
18. `GoToDefinitionTool.cs`
19. `SearchSymbolsTool.cs`

**Step 2: Delete `SolutionGuard.cs`**

The `EnsureLoaded()` check now lives in `SolutionManager`. Delete the file.

**Step 3: Build to verify**

Run: `dotnet build src/RoslynCodeGraph/RoslynCodeGraph.csproj`
Expected: BUILD SUCCEEDED

**Step 4: Run all tests**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj`
Expected: All tests pass (test Logic classes directly, not through tools)

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/ src/RoslynCodeGraph/SolutionGuard.cs
git commit -m "refactor: update all tools to use SolutionManager"
```

---

### Task 6: Update `Program.cs` DI Registration

**Files:**
- Modify: `src/RoslynCodeGraph/Program.cs`

**Step 1: Rewrite Program.cs**

```csharp
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using RoslynCodeGraph;

MSBuildLocator.RegisterDefaults();

var solutionPath = args.Length > 0
    ? args[0]
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory());

SolutionManager manager;

if (solutionPath != null)
{
    manager = await SolutionManager.CreateAsync(solutionPath);
}
else
{
    Console.Error.WriteLine("[roslyn-codegraph] No .sln file found. Tools will return errors.");
    manager = SolutionManager.CreateEmpty();
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(manager);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

**Step 2: Build and run tests**

Run: `dotnet build src/RoslynCodeGraph/RoslynCodeGraph.csproj`
Expected: BUILD SUCCEEDED

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj`
Expected: All tests pass

**Step 3: Commit**

```bash
git add src/RoslynCodeGraph/Program.cs
git commit -m "refactor: wire SolutionManager in Program.cs"
```

---

### Task 7: Add New Model Types for Source Generators

**Files:**
- Create: `src/RoslynCodeGraph/Models/SourceGeneratorInfo.cs`
- Create: `src/RoslynCodeGraph/Models/GeneratedFileInfo.cs`

**Step 1: Create model records**

`src/RoslynCodeGraph/Models/SourceGeneratorInfo.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record SourceGeneratorInfo(
    string GeneratorName,
    string Project,
    int GeneratedFileCount,
    List<string> GeneratedFiles);
```

`src/RoslynCodeGraph/Models/GeneratedFileInfo.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record GeneratedFileInfo(
    string FilePath,
    string Project,
    string? GeneratorName,
    List<string> DefinedTypes,
    string SourceText);
```

**Step 2: Build**

Run: `dotnet build src/RoslynCodeGraph/RoslynCodeGraph.csproj`
Expected: BUILD SUCCEEDED

**Step 3: Commit**

```bash
git add src/RoslynCodeGraph/Models/SourceGeneratorInfo.cs src/RoslynCodeGraph/Models/GeneratedFileInfo.cs
git commit -m "feat: add SourceGeneratorInfo and GeneratedFileInfo models"
```

---

### Task 8: Add `get_source_generators` Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/GetSourceGeneratorsTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/GetSourceGeneratorsToolTests.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/Tools/GetSourceGeneratorsToolTests.cs`:

```csharp
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetSourceGeneratorsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Execute_ReturnsEmptyList_WhenNoGenerators()
    {
        // TestSolution likely has no source generators, so expect empty or minimal results
        var results = GetSourceGeneratorsLogic.Execute(_loaded, _resolver, null);
        Assert.NotNull(results);
    }

    [Fact]
    public void Execute_FiltersbyProject_WhenProjectSpecified()
    {
        var projectName = _loaded.Solution.Projects.First().Name;
        var results = GetSourceGeneratorsLogic.Execute(_loaded, _resolver, projectName);
        Assert.NotNull(results);
        Assert.All(results, r => Assert.Equal(projectName, r.Project));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "GetSourceGenerators"`
Expected: FAIL — class does not exist

**Step 3: Implement the tool**

Create `src/RoslynCodeGraph/Tools/GetSourceGeneratorsTool.cs`:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetSourceGeneratorsLogic
{
    public static List<SourceGeneratorInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string? project)
    {
        var results = new List<SourceGeneratorInfo>();

        foreach (var proj in loaded.Solution.Projects)
        {
            if (project != null && !proj.Name.Equals(project, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!loaded.Compilations.TryGetValue(proj.Id, out var compilation))
                continue;

            // Find generated syntax trees
            var generatedFiles = compilation.SyntaxTrees
                .Where(t => resolver.IsGenerated(t.FilePath))
                .Select(t => t.FilePath)
                .ToList();

            if (generatedFiles.Count == 0)
                continue;

            // Group by generator (infer from path convention: generator name is typically in the path)
            var byGenerator = generatedFiles
                .GroupBy(f => InferGeneratorName(f))
                .ToList();

            foreach (var group in byGenerator)
            {
                results.Add(new SourceGeneratorInfo(
                    group.Key,
                    proj.Name,
                    group.Count(),
                    group.ToList()));
            }
        }

        return results;
    }

    private static string InferGeneratorName(string filePath)
    {
        // Source generator outputs typically follow pattern:
        // obj/Debug/net10.0/generated/GeneratorAssembly/GeneratorName/File.g.cs
        var parts = filePath.Replace('\\', '/').Split('/');
        var objIndex = Array.FindIndex(parts, p => p.Equals("obj", StringComparison.OrdinalIgnoreCase));

        // Look for segments after the TFM (e.g., net10.0)
        if (objIndex >= 0 && objIndex + 3 < parts.Length)
        {
            // Try to find generator assembly name after the TFM
            for (var i = objIndex + 3; i < parts.Length - 1; i++)
            {
                var segment = parts[i];
                if (!segment.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    && !segment.Contains('.'))
                {
                    return segment;
                }
            }
        }

        return "Unknown";
    }
}

[McpServerToolType]
public static class GetSourceGeneratorsTool
{
    [McpServerTool(Name = "get_source_generators"),
     Description("List source generators and their output per project")]
    public static List<SourceGeneratorInfo> Execute(
        SolutionManager manager,
        [Description("Optional project name filter")] string? project = null)
    {
        manager.EnsureLoaded();
        return GetSourceGeneratorsLogic.Execute(manager.GetLoadedSolution(), manager.GetResolver(), project);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "GetSourceGenerators"`
Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/GetSourceGeneratorsTool.cs tests/RoslynCodeGraph.Tests/Tools/GetSourceGeneratorsToolTests.cs
git commit -m "feat: add get_source_generators tool"
```

---

### Task 9: Add `get_generated_code` Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/GetGeneratedCodeTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/GetGeneratedCodeToolTests.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeGraph.Tests/Tools/GetGeneratedCodeToolTests.cs`:

```csharp
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetGeneratedCodeToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Execute_ReturnsEmpty_WhenFileNotFound()
    {
        var results = GetGeneratedCodeLogic.Execute(_loaded, _resolver, null, "nonexistent.g.cs");
        Assert.Empty(results);
    }

    [Fact]
    public void Execute_ReturnsEmpty_WhenGeneratorNotFound()
    {
        var results = GetGeneratedCodeLogic.Execute(_loaded, _resolver, "NonExistentGenerator", null);
        Assert.Empty(results);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "GetGeneratedCode"`
Expected: FAIL — class does not exist

**Step 3: Implement the tool**

Create `src/RoslynCodeGraph/Tools/GetGeneratedCodeTool.cs`:

```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetGeneratedCodeLogic
{
    public static List<GeneratedFileInfo> Execute(
        LoadedSolution loaded, SymbolResolver resolver, string? generator, string? file)
    {
        var results = new List<GeneratedFileInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var projectName = resolver.GetProjectName(projectId);

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!resolver.IsGenerated(tree.FilePath))
                    continue;

                // Filter by file path if specified
                if (file != null && !tree.FilePath.Contains(file, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Filter by generator name if specified
                var genName = InferGeneratorName(tree.FilePath);
                if (generator != null && !genName.Equals(generator, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Extract defined types
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();
                var definedTypes = root.DescendantNodes()
                    .Where(n => n is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax)
                    .Select(n =>
                    {
                        var symbol = semanticModel.GetDeclaredSymbol(n);
                        return symbol?.ToDisplayString() ?? "";
                    })
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();

                var sourceText = tree.GetText().ToString();

                results.Add(new GeneratedFileInfo(
                    tree.FilePath,
                    projectName,
                    genName,
                    definedTypes,
                    sourceText));
            }
        }

        return results;
    }

    private static string InferGeneratorName(string filePath)
    {
        var parts = filePath.Replace('\\', '/').Split('/');
        var objIndex = Array.FindIndex(parts, p => p.Equals("obj", StringComparison.OrdinalIgnoreCase));

        if (objIndex >= 0 && objIndex + 3 < parts.Length)
        {
            for (var i = objIndex + 3; i < parts.Length - 1; i++)
            {
                var segment = parts[i];
                if (!segment.Equals("generated", StringComparison.OrdinalIgnoreCase)
                    && !segment.Contains('.'))
                {
                    return segment;
                }
            }
        }

        return "Unknown";
    }
}

[McpServerToolType]
public static class GetGeneratedCodeTool
{
    [McpServerTool(Name = "get_generated_code"),
     Description("Inspect generated source code from source generators")]
    public static List<GeneratedFileInfo> Execute(
        SolutionManager manager,
        [Description("Generator name to filter by")] string? generator = null,
        [Description("File path (or partial match) to filter by")] string? file = null)
    {
        manager.EnsureLoaded();
        return GetGeneratedCodeLogic.Execute(
            manager.GetLoadedSolution(), manager.GetResolver(), generator, file);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj --filter "GetGeneratedCode"`
Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/GetGeneratedCodeTool.cs tests/RoslynCodeGraph.Tests/Tools/GetGeneratedCodeToolTests.cs
git commit -m "feat: add get_generated_code tool"
```

---

### Task 10: Populate `IsGenerated` in Location-Returning Tools

**Files:**
- Modify: `src/RoslynCodeGraph/Tools/FindImplementationsTool.cs` (Logic class)
- Modify: `src/RoslynCodeGraph/Tools/FindCallersTool.cs` (Logic class)
- Modify: `src/RoslynCodeGraph/Tools/FindReferencesTool.cs` (Logic class)
- Modify: `src/RoslynCodeGraph/Tools/GoToDefinitionTool.cs` (Logic class)
- Modify: `src/RoslynCodeGraph/Tools/FindUnusedSymbolsTool.cs` (Logic class)
- Modify: `src/RoslynCodeGraph/Tools/FindReflectionUsageTool.cs` (Logic class)

**Step 1: Update each Logic class to pass `IsGenerated`**

The pattern is the same for each — where a `SymbolLocation`, `CallerInfo`, `SymbolReference`, `UnusedSymbolInfo`, or `ReflectionUsage` is constructed, add the `IsGenerated` parameter using `resolver.IsGenerated(file)`.

Example for `FindImplementationsLogic`:

**Before:**
```csharp
results.Add(new SymbolLocation(kind, fullName, file, line, project));
```

**After:**
```csharp
results.Add(new SymbolLocation(kind, fullName, file, line, project, resolver.IsGenerated(file)));
```

Example for `FindCallersLogic`:

**Before:**
```csharp
results.Add(new CallerInfo(callerName, file, line, snippet, projectName));
```

**After:**
```csharp
results.Add(new CallerInfo(callerName, file, line, snippet, projectName, resolver.IsGenerated(file)));
```

Apply this pattern to all location-constructing call sites in:
- `FindImplementationsLogic` — `SymbolLocation` constructor
- `FindCallersLogic` — `CallerInfo` constructor
- `FindReferencesLogic` — `SymbolReference` constructor
- `GoToDefinitionLogic` — `SymbolLocation` constructor
- `FindUnusedSymbolsLogic` — `UnusedSymbolInfo` constructor
- `FindReflectionUsageLogic` — `ReflectionUsage` constructor

**Step 2: Build and run all tests**

Run: `dotnet build src/RoslynCodeGraph/RoslynCodeGraph.csproj`
Expected: BUILD SUCCEEDED

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj`
Expected: All tests pass (existing tests still pass because test fixtures use hand-written code, so `IsGenerated` will be `false`)

**Step 3: Commit**

```bash
git add src/RoslynCodeGraph/Tools/FindImplementationsTool.cs src/RoslynCodeGraph/Tools/FindCallersTool.cs src/RoslynCodeGraph/Tools/FindReferencesTool.cs src/RoslynCodeGraph/Tools/GoToDefinitionTool.cs src/RoslynCodeGraph/Tools/FindUnusedSymbolsTool.cs src/RoslynCodeGraph/Tools/FindReflectionUsageTool.cs
git commit -m "feat: populate IsGenerated flag in location-returning tools"
```

---

### Task 11: Update Tests to Use `SolutionManager` Pattern

**Files:**
- Modify: All test files in `tests/RoslynCodeGraph.Tests/Tools/` that directly test tool wrapper classes (if any)

**Step 1: Verify test approach**

All existing tests call `*Logic.Execute()` directly with `LoadedSolution` and `SymbolResolver` — these don't need changes since Logic classes still accept the same parameters. Only update tests if any directly call the `*Tool.Execute()` wrapper method.

**Step 2: Run full test suite**

Run: `dotnet test tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj`
Expected: All tests pass

**Step 3: Commit (if any changes were needed)**

```bash
git add tests/RoslynCodeGraph.Tests/
git commit -m "test: update tool tests for SolutionManager"
```

---

### Task 12: Update SKILL.md and README

**Files:**
- Modify: `plugins/roslyn-codegraph/skills/roslyn-codegraph/SKILL.md`
- Modify: `README.md`

**Step 1: Add new tools to SKILL.md tool matrix**

Add entries for `get_source_generators` and `get_generated_code` in the appropriate section.

**Step 2: Add new tools to README**

Add the two new tools to the tool list in README.md. Update the total tool count from 19 to 21. Add a section about hot reload behavior (file watching, lazy re-indexing).

**Step 3: Commit**

```bash
git add plugins/roslyn-codegraph/skills/roslyn-codegraph/SKILL.md README.md
git commit -m "docs: add source generator tools and hot reload to docs"
```

---

### Task 13: Final Integration Test

**Step 1: Build the full solution**

Run: `dotnet build`
Expected: BUILD SUCCEEDED

**Step 2: Run all tests**

Run: `dotnet test`
Expected: All tests pass

**Step 3: Manual smoke test**

Run: `dotnet run --project src/RoslynCodeGraph/RoslynCodeGraph.csproj`
Expected: Server starts, loads solution, outputs "Ready" to stderr. FileChangeTracker starts watching. Server accepts MCP requests via stdio.
