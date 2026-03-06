# Roslyn Code Graph MCP Server — Design Document

**Date:** 2026-03-06
**Status:** Approved
**Repo:** `MarcelRoozekrans/roslyn-codegraph-mcp` (separate from superpowers-extensions)

## Problem Statement

On large .NET codebases (900k+ lines), Claude Code loses track during implementation. Grep/Glob are text search — they don't understand C# semantics like interfaces, DI, inheritance, or reflection. This leads to architectural blindness where subagents don't understand how files connect in the larger system.

## Solution

A Roslyn-based MCP server that loads the .NET solution, compiles it, and exposes structured semantic queries as MCP tools. Paired with a skill that enhances brainstorming and refactor-analysis with always-on architectural context.

## Repository Structure

```
roslyn-codegraph-mcp/
├── .claude-plugin/
│   └── marketplace.json          # Plugin marketplace manifest
├── plugins/
│   └── roslyn-codegraph/
│       ├── .claude-plugin/
│       │   └── plugin.json       # Plugin manifest + MCP server config
│       ├── bootstrap.sh          # Auto-installs dotnet tool on first run
│       ├── bootstrap.ps1         # Windows equivalent
│       └── skills/
│           └── roslyn-codegraph/
│               └── SKILL.md      # Brainstorming + refactor-analysis enhancement
├── src/
│   └── RoslynCodeGraph/
│       ├── RoslynCodeGraph.csproj
│       ├── Program.cs            # Entry point, stdio MCP transport
│       ├── SolutionLoader.cs     # MSBuildWorkspace loading + progress
│       ├── Tools/
│       │   ├── FindImplementations.cs
│       │   ├── FindCallers.cs
│       │   ├── GetTypeHierarchy.cs
│       │   ├── GetDiRegistrations.cs
│       │   ├── GetProjectDependencies.cs
│       │   ├── GetSymbolContext.cs
│       │   └── FindReflectionUsage.cs
│       └── Models/               # Shared response types
├── tests/
│   └── RoslynCodeGraph.Tests/
├── README.md
├── LICENSE
└── .gitignore
```

## Distribution

### Installation

```bash
claude install gh:MarcelRoozekrans/roslyn-codegraph-mcp
claude plugin install roslyn-codegraph
```

The plugin includes a bootstrap script that auto-installs the .NET global tool on first run if not already present. No separate `dotnet tool install` step required.

### Plugin Configuration

```json
{
  "name": "roslyn-codegraph",
  "description": "Roslyn-based code graph intelligence for .NET codebases.",
  "author": {
    "name": "Marcel Roozekrans"
  },
  "mcp_servers": {
    "roslyn-codegraph": {
      "command": "bootstrap",
      "args": [],
      "transport": "stdio"
    }
  }
}
```

The bootstrap script:
1. Checks if `roslyn-codegraph-mcp` is available on PATH
2. If not, runs `dotnet tool install -g roslyn-codegraph-mcp`
3. Launches `roslyn-codegraph-mcp` via stdio

## MCP Server

### Solution Loading

1. Server starts via stdio MCP transport
2. Scans working directory (and parents) for `.sln` files
3. If multiple found, picks the one closest to the working directory
4. Opens `MSBuildWorkspace`, loads projects one-by-one with stderr progress
5. Compiles the full solution, builds semantic model
6. Reports summary, begins accepting tool calls

### Startup Progress (stderr)

```
[roslyn-codegraph] Discovering solution files...
[roslyn-codegraph] Found: MyApp.sln (12 projects)
[roslyn-codegraph] Loading project  1/12: MyApp.Domain
[roslyn-codegraph] Loading project  2/12: MyApp.Infrastructure
...
[roslyn-codegraph] Loading project 12/12: MyApp.Tests.Integration
[roslyn-codegraph] Compiling solution...
[roslyn-codegraph] Ready. 847 types indexed across 12 projects.
```

### Error Handling

- **No `.sln` found:** Server starts, all tools return an error suggesting `--solution` flag
- **Build warnings:** Logged to stderr, don't block startup
- **Build errors:** Logged with count. Server still starts — Roslyn provides partial semantic models. Tools include a warning when querying projects with errors
- **Project load failure:** Individual failures logged, don't block other projects

### Lifecycle

- Single compilation on startup. No hot reload in v1.0
- User restarts the MCP server if code changes significantly
- Memory: ~500MB-1GB for a 900k-line solution, acceptable for development machines

## MCP Tools (7)

All tools accept a `symbol` parameter as a simple name (`"UserService"`) or fully qualified (`"MyApp.Services.UserService"`). Ambiguous names return all matches with full names for disambiguation.

### find_implementations

- **Input:** `{ "symbol": "IUserService" }`
- **Purpose:** Find all classes/structs implementing an interface or extending a class
- **Returns:** `[{ "type": "class", "fullName": "MyApp.Services.UserService", "file": "src/Services/UserService.cs", "line": 15, "project": "MyApp.Services" }]`

### find_callers

- **Input:** `{ "symbol": "UserService.GetById" }`
- **Purpose:** Find every call site for a method, property, or constructor
- **Returns:** `[{ "caller": "UserController.Get", "file": "src/Controllers/UserController.cs", "line": 42, "snippet": "var user = _userService.GetById(id);", "project": "MyApp.Api" }]`

### get_type_hierarchy

- **Input:** `{ "symbol": "BaseController" }`
- **Purpose:** Walk up (base classes, interfaces) and down (derived types)
- **Returns:** `{ "bases": [...], "interfaces": [...], "derived": [...] }` with file/line for each

### get_di_registrations

- **Input:** `{ "symbol": "IUserService" }`
- **Purpose:** Scan `IServiceCollection` extension methods for DI registrations
- **Returns:** `[{ "service": "IUserService", "implementation": "UserService", "lifetime": "Scoped", "file": "src/Startup.cs", "line": 28 }]`

### get_project_dependencies

- **Input:** `{ "project": "Api.csproj" }`
- **Purpose:** Return the project reference graph
- **Returns:** `{ "direct": [...], "transitive": [...] }` with project names and paths

### get_symbol_context

- **Input:** `{ "symbol": "UserService" }`
- **Purpose:** One-shot context dump for a type
- **Returns:** `{ "fullName": "...", "namespace": "...", "project": "...", "file": "...", "line": 0, "baseClass": "...", "interfaces": [...], "injectedDependencies": [...], "publicMembers": [...] }`

### find_reflection_usage

- **Input:** `{ "symbol": "UserService" }` (optional — omit to scan entire solution)
- **Purpose:** Detect dynamic/reflection-based usage
- **Detects:** `Type.GetType("...")`, `Activator.CreateInstance`, `MethodInfo.Invoke`, assembly scanning, attribute-based discovery
- **Returns:** `[{ "kind": "dynamic_instantiation", "target": "UserService", "file": "...", "line": 0, "snippet": "..." }]`
- **Kinds:** `dynamic_instantiation`, `method_invoke`, `assembly_scan`, `attribute_discovery`

## Skill Design

The skill (`SKILL.md`) provides guidance on when and how to use the Roslyn MCP tools. It is standalone and has no external dependencies.

### When to Use

The skill instructs Claude to use these tools **instead of Grep/Glob** for .NET code structure queries:

- **Understanding a codebase** — `get_project_dependencies` for architecture, `get_symbol_context` for type details, `get_type_hierarchy` for inheritance
- **Finding dependencies** — `find_callers` for call sites, `find_implementations` for interface implementors, `get_di_registrations` for DI wiring, `find_reflection_usage` for hidden coupling
- **Planning changes** — combine all tools to assess impact before modifying code

### Detection

The skill checks for tool availability. If `find_implementations` is not in the available MCP tools, the skill is inert. No errors, no degradation.

## Performance

The server is optimized for minimal allocations and fast queries by caching all type information at construction time in `SymbolResolver`:

- **Cached type lists** — All types across all compilations are enumerated once and stored in a flat list, deduplicated by fully-qualified name
- **Dictionary lookups** — Types are indexed by both simple name and fully-qualified name for O(1) `FindNamedTypes()` instead of O(all types)
- **File-to-project map** — `GetProjectName()` uses a pre-built `Dictionary<string, string>` instead of linear-scanning all projects and documents
- **Span-based comparisons** — DI registration matching uses `ReadOnlySpan<char>` to avoid `string.Split()` allocations
- **Value tuple deduplication** — `DistinctBy` uses value tuples instead of string interpolation

### Benchmark Results (i9-12900HK, .NET 10.0.3, Release)

| Tool | Latency | Memory |
|------|--------:|-------:|
| `find_implementations` | 187 µs | 95 KB |
| `find_callers` | 192 µs | 32 KB |
| `get_type_hierarchy` | 139 µs | 1.1 KB |
| `get_symbol_context` | 1.3 µs | 1.0 KB |
| `get_di_registrations` | 79 µs | 13 KB |
| `get_project_dependencies` | 0.4 µs | 1.2 KB |
| `find_reflection_usage` | 98 µs | 15 KB |
| Solution loading (one-time) | 1,049 ms | 8 MB |

## Out of Scope (v1.0)

- Hot reload / file watching
- Decision tracking / long-term memory integration (future superpowers-extensions skill)
- Cross-solution analysis
- NuGet package analysis
- Source generators
