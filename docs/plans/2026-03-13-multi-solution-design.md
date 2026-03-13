# Multi-Solution Support

## Problem

When the MCP server is installed as a global dotnet tool, a single process is shared across all VS Code windows. That process is started with one hardcoded solution path, so any second workspace ends up analysing the wrong solution.

## Goal

Allow the server to load N solutions at startup and expose two tools so Claude can select the right solution at the start of each session. All 22 existing tools remain unchanged.

## Architecture

### `MultiSolutionManager`

New class that owns a `Dictionary<string, SolutionManager>` keyed by normalised solution path and tracks the currently active key.

Exposes the same public API as `SolutionManager`:

- `EnsureLoaded()`
- `GetLoadedSolution()`
- `GetResolver()`
- `WaitForWarmupAsync()`
- `ForceReloadAsync()`

Each method delegates to the active `SolutionManager`. All 22 existing tools change only the injected parameter type from `SolutionManager` → `MultiSolutionManager`.

### Startup (`Program.cs`)

- Accept 0..N solution paths as CLI args (unchanged single-path and auto-discovery behaviour preserved when 0 or 1 arg is given).
- Create one `SolutionManager` per path (warm-up runs in parallel as today).
- Register `MultiSolutionManager` as the DI singleton.
- Default active solution: first arg (or auto-discovered path).

### DI

`builder.Services.AddSingleton(multiManager)` — identical to today, just a different type.

## New Tools

### `list_solutions`

Returns a list of all loaded solutions:

```json
[
  { "path": "C:/A/A.sln", "isActive": true,  "projectCount": 5, "status": "ready"   },
  { "path": "C:/B/B.sln", "isActive": false, "projectCount": 3, "status": "loading" }
]
```

### `set_active_solution`

Parameter: `name` (partial, case-insensitive match against the solution file name or full path).

- Exact match preferred; fails with a clear error if ambiguous or not found.
- Returns the full path of the newly active solution.

## Configuration Example

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "type": "stdio",
      "command": "roslyn-codelens-mcp",
      "args": [
        "C:/Projects/ProjectA/A.sln",
        "C:/Projects/ProjectB/B.sln"
      ]
    }
  }
}
```

## Backward Compatibility

- Zero args → auto-discovery (unchanged).
- One arg → single solution loaded, `MultiSolutionManager` wraps it (unchanged behaviour).
- Multiple args → new multi-solution behaviour.
