---
title: FAQ
---

# FAQ

## Why not just grep the source files?

Grep finds text patterns. `roslyn-codelens-mcp` understands semantic structure — types, interfaces, inheritance, call graphs, DI registrations. Grep on `IGreeter` misses implementations that store the service under a different variable name, and can't answer "what calls this method" or "what implements this interface."

## Does it support multiple solutions?

Yes. Use `list_solutions`, `set_active_solution`, `load_solution`, and `unload_solution` to manage multiple solutions. Only one is active at a time for tool calls.

## How does hot reload work?

The server runs a `FileChangeTracker` that watches for file system changes. Saving a `.cs` file updates the Roslyn workspace automatically — no `sync_documents` call needed.

## Why does `find_references` return nothing for an external type?

The package must be referenced by at least one project in the loaded solution. Call `get_nuget_dependencies` to check, then make sure the assembly name matches exactly.

## How fast is it?

Solution load takes 5–30 seconds (once at startup). After that, most calls run in under 1ms–3ms. Heavier operations:

| Tool | Typical latency |
|------|----------------|
| `find_naming_violations` | ~8ms |
| `find_references` | ~3ms |
| `analyze_change_impact` | ~4ms |
| `search_symbols` | ~1.4ms |

Full benchmark results are in the [repository](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/tree/main/benchmarks).

## What .NET versions are supported?

The server targets .NET 10 and .NET 11. SDK-style projects (`<Project Sdk="...">`) can target any .NET version, including .NET Framework.

## Are legacy (non-SDK-style) .NET Framework projects supported?

Yes, during a full solution load on Windows with Visual Studio MSBuild and the project's .NET Framework targeting pack. Call `load_solution` with just the solution path. If the solution is already loaded with a filter, call `unload_solution` first.

Filtered loads, per-project fallback loads, and loads on other platforms skip legacy projects. Check `list_solutions` for the skipped projects and their reasons.

## Why is `list_solutions` showing `SkippedProjects` for my solution?

Those are projects that couldn't be loaded by MSBuildWorkspace. Each entry tells you the reason — typically `Legacy` (non-SDK-style) or `Missing` (referenced from the `.sln` but the file doesn't exist on disk). All other projects in the solution loaded normally and can be queried by any tool.
