# Hot Reload & Source Generator Support Design

**Date:** 2026-03-07
**Status:** Approved
**Scope:** Lazy file-watching with project-level incremental rebuild + source generator indexing

## Problem

1. **Stale results** — After editing code, the MCP server returns results from the old compilation. Users must restart the server to pick up changes.
2. **Missing generated code** — Source generator output (e.g., `System.Text.Json`, Mediator, gRPC) is compiled into syntax trees but not explicitly tracked. No way to inspect which generators exist or what they produce.

## Approach

**Lazy staleness with project-level rebuild** — `FileSystemWatcher` monitors source files, marks affected projects as stale. On the next tool query, only stale projects (and transitive dependents) are re-compiled. Source generator output is indexed into all existing caches and surfaced via two new tools.

## Design

### 1. File Watching & Staleness Tracking

**New class: `FileChangeTracker`**

- `FileSystemWatcher` monitors the solution directory recursively for `*.cs`, `*.csproj`, `*.props`, `*.targets` changes
- Maintains a `HashSet<ProjectId>` of stale projects
- When a `.cs` file changes: uses file-to-project mapping to identify the owning project, marks it stale
- When a `.csproj`/`.props`/`.targets` changes: marks the corresponding project stale
- Builds a **reverse project dependency graph** at startup. When a project is marked stale, all its downstream dependents are transitively marked stale too
- Debounces rapid changes (200ms) to batch saves from IDE auto-format, branch switches, etc.
- Exposes `bool HasStaleProjects` and `IReadOnlySet<ProjectId> StaleProjectIds`

### 2. Lazy Re-indexing

**New class: `SolutionManager`** — replaces the current direct `LoadedSolution` + `SymbolResolver` singletons.

**Responsibilities:**
- Holds the current `LoadedSolution` and `SymbolResolver`
- Owns the `FileChangeTracker`
- Exposes `GetResolver()` and `GetLoadedSolution()` methods that tools call instead of receiving injected singletons
- On each call, checks `FileChangeTracker.HasStaleProjects`. If stale:
  1. Re-opens the `MSBuildWorkspace` solution to pick up file changes
  2. Re-compiles only the stale projects (and their transitive dependents)
  3. Merges updated compilations back into the `Compilations` dictionary
  4. Rebuilds `SymbolResolver` from the updated `LoadedSolution`
  5. Clears the stale set
- Thread-safe via a simple lock — concurrent tool calls wait for the rebuild to finish

**Impact on existing tools:**
- Tools change from injecting `LoadedSolution` + `SymbolResolver` to injecting `SolutionManager`
- Each tool calls `solutionManager.GetResolver()` / `solutionManager.GetLoadedSolution()` at the start
- `SolutionGuard.EnsureLoaded()` moves into `SolutionManager`

**DI registration in Program.cs:**
- `LoadedSolution` and `SymbolResolver` are no longer registered directly
- `SolutionManager` registered as singleton, receives the initial `LoadedSolution`

### 3. Source Generator Support

**Generated code detection in `SymbolResolver`:**
- During indexing, classify each `SyntaxTree` as generated or hand-written:
  - Path contains `/obj/` or `\obj\` → generated
  - Path is empty or null → generated
  - `GeneratedCodeAttribute` on types within the tree → generated
- Store a `HashSet<string> _generatedFilePaths` for fast lookup
- All generated symbols indexed into existing caches (`_typesBySimpleName`, `_interfaceImplementors`, etc.) — they participate in every tool automatically
- Expose `bool IsGenerated(string filePath)` method

**New tool: `get_source_generators`**
- Parameters: optional `project` name filter
- Returns per project:
  - Generator name (from `AnalyzerReference.Display` or assembly name)
  - Number of generated syntax trees
  - List of generated file paths with the types defined in each
- Implementation: iterate `Project.AnalyzerReferences`, cross-reference with `_generatedFilePaths` and syntax trees

**New tool: `get_generated_code`**
- Parameters: `generator` name or `file` path (at least one required)
- Returns the generated source text and the types/members defined within

**Existing tool enhancements:**
- Tools that return locations (`find_callers`, `find_references`, `go_to_definition`, `find_implementations`, `find_unused_symbols`) gain an `IsGenerated` flag in their response — so Claude knows the file can't be edited directly

### 4. Data Flow

**Startup sequence:**
1. `MSBuildLocator.RegisterDefaults()`
2. Discover solution path (unchanged)
3. `SolutionLoader.LoadAsync()` (unchanged)
4. Create `SolutionManager(loaded, solutionPath)` — wraps loaded solution, builds initial `SymbolResolver`, starts `FileChangeTracker`
5. Register `SolutionManager` as singleton in DI
6. MCP server starts

**Query flow:**
```
Tool invoked → SolutionManager.GetResolver()
  → FileChangeTracker.HasStaleProjects?
    → No: return cached SymbolResolver
    → Yes: re-compile stale projects, rebuild SymbolResolver, clear stale set, return new SymbolResolver
```

**File change flow:**
```
File saved → FileSystemWatcher fires
  → 200ms debounce
  → Map file to ProjectId
  → Mark project + transitive dependents as stale
  → (nothing else until next tool query)
```

## File Changes

**New files:**
- `FileChangeTracker.cs` — watcher + stale tracking + reverse dependency graph
- `SolutionManager.cs` — orchestrates lazy rebuild
- `Tools/GetSourceGeneratorsTool.cs` — list generators and their output
- `Tools/GetGeneratedCodeTool.cs` — inspect generated source

**Modified files:**
- `Program.cs` — swap DI registrations to `SolutionManager`
- `SymbolResolver.cs` — add generated file detection, `_generatedFilePaths`, `IsGenerated()` method
- All 19 existing tool files — inject `SolutionManager` instead of `LoadedSolution`/`SymbolResolver`
- `SymbolLocation.cs`, `CallerInfo.cs` — add `IsGenerated` property
- Location-returning tools — populate `IsGenerated` flag

## Out of Scope

- Cross-solution analysis
- Background eager re-compilation
- Explicit `GeneratorDriver` invocation (rely on Roslyn compilation output)
