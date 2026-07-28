---
title: Configuration
sidebar_position: 3
---

# Configuration

## Solution path

The server needs to know which `.sln` or `.slnx` file to load.

**Option 1: arguments** — solution paths are passed bare, not behind a flag.
```json
"args": ["/path/to/Solution.sln"]
```

**Option 2: automatic discovery** — pass no arguments and the server searches its
working directory for a `.sln`/`.slnx`.

**Option 3: at runtime** — call `load_solution` after the server starts.

## Multiple solutions

`roslyn-codelens-mcp` has a built-in solution manager. Pass several paths at
startup, or load more at runtime:

```
Use load_solution with path /path/to/OtherSolution.sln
Use list_solutions to see what's loaded
Use set_active_solution with name OtherSolution
```

The two tools are not interchangeable: `load_solution` takes a **path** and opens
a solution the server has not seen; `set_active_solution` takes a partial,
case-insensitive **name** and only switches between solutions already loaded.

Only one solution is "active" at a time. All tool calls operate on the active solution.

## Automatic hot reload

The server watches for file changes via `FileChangeTracker`. When you edit and save a `.cs` file, the server updates its index automatically — no manual sync needed.

DLL changes (e.g. after `dotnet build`) also invalidate the IL cache automatically.

## Force reload

If the solution gets into a bad state:

```
Use rebuild_solution to force a full reload
```
