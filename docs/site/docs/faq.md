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

The server targets .NET 10 and can analyse solutions targeting any .NET version (net48, net6.0, net8.0, net10.0, etc.) — **as long as the projects use the SDK-style csproj format** (`<Project Sdk="...">`).

## Are legacy (non-SDK-style) .NET Framework projects supported?

**No, and they will be skipped — but the rest of the solution still loads.**

Legacy projects use the old MSBuild XML format (`<Project ToolsVersion="..." xmlns="http://schemas.microsoft.com/developer/msbuild/2003">`) and rely on the .NET Framework MSBuild that ships with Visual Studio. The .NET SDK does not include those targets, so MSBuildWorkspace cannot load them under `dotnet run`.

What the server does when it encounters them:

- Pre-classifies every `.csproj` in the solution before invoking MSBuild.
- Loads all SDK-style projects normally — every tool works for those.
- Records each legacy project in `LoadedSolution.SkippedProjects`.
- Exposes the skipped list through `list_solutions` (the `SkippedProjects` array, with `Name`, `Path`, `Kind`, and `Reason` per project), and includes a summary in the return message of `load_solution`.

To analyse a legacy project, either convert it to SDK-style csproj format, or run the server from a Visual Studio Developer Command Prompt where the full MSBuild toolchain is on `PATH`.

## Why is `list_solutions` showing `SkippedProjects` for my solution?

Those are projects that couldn't be loaded by MSBuildWorkspace. Each entry tells you the reason — typically `Legacy` (non-SDK-style) or `Missing` (referenced from the `.sln` but the file doesn't exist on disk). All other projects in the solution loaded normally and can be queried by any tool.
