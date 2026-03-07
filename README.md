# Roslyn Code Graph MCP Server

A Roslyn-based MCP server that provides 21 semantic code intelligence tools for .NET codebases. Designed for use with Claude Code to understand type hierarchies, call sites, DI registrations, and reflection usage.

## Features

- **find_implementations** — Find all classes/structs implementing an interface or extending a class
- **find_callers** — Find every call site for a method, property, or constructor
- **get_type_hierarchy** — Walk base classes, interfaces, and derived types
- **get_di_registrations** — Scan for DI service registrations
- **get_project_dependencies** — Get the project reference graph
- **get_symbol_context** — One-shot context dump for any type
- **find_reflection_usage** — Detect dynamic/reflection-based usage
- **find_references** — Find all references to any symbol (types, methods, properties, fields, events)
- **go_to_definition** — Find the source file and line where a symbol is defined
- **get_diagnostics** — List compiler errors, warnings, and Roslyn analyzer diagnostics
- **get_code_fixes** — Get available code fixes with structured text edits for any diagnostic
- **search_symbols** — Fuzzy workspace symbol search by name
- **get_nuget_dependencies** — List NuGet package references per project
- **find_attribute_usages** — Find types and members decorated with a specific attribute
- **find_circular_dependencies** — Detect cycles in project or namespace dependency graphs
- **get_complexity_metrics** — Cyclomatic complexity analysis per method
- **find_naming_violations** — Check .NET naming convention compliance
- **find_large_classes** — Find oversized types by member or line count
- **find_unused_symbols** — Dead code detection via reference analysis
- **get_source_generators** — List source generators and their output per project
- **get_generated_code** — Inspect generated source code from source generators

## Installation

### As a Claude Code Plugin

```bash
claude install gh:MarcelRoozekrans/roslyn-codegraph-mcp
```

### As a .NET Global Tool

```bash
dotnet tool install -g RoslynCodeGraph.Mcp
```

### Manual MCP Configuration

Add to your Claude Code MCP settings:

```json
{
  "mcpServers": {
    "roslyn-codegraph": {
      "command": "roslyn-codegraph-mcp",
      "args": [],
      "transport": "stdio"
    }
  }
}
```

## Usage

The server automatically discovers `.sln` files by walking up from the current directory. You can also pass a solution path directly:

```bash
roslyn-codegraph-mcp /path/to/MySolution.sln
```

## Performance

All type lookups use pre-built reverse inheritance maps, member indexes, and attribute indexes for O(1) access. Benchmarked on an i9-12900HK with .NET 10.0.3:

| Tool | Latency | Memory |
|------|--------:|-------:|
| `get_project_dependencies` | 325 ns | 1.2 KB |
| `find_circular_dependencies` | 336 ns | 1.3 KB |
| `go_to_definition` | 389 ns | 528 B |
| `find_implementations` | 705 ns | 624 B |
| `get_type_hierarchy` | 755 ns | 816 B |
| `get_symbol_context` | 1.3 µs | 1.0 KB |
| `find_attribute_usages` | 8.9 µs | 312 B |
| `get_diagnostics` | 35 µs | 23 KB |
| `get_complexity_metrics` | 64 µs | 5.7 KB |
| `get_di_registrations` | 73 µs | 13 KB |
| `get_nuget_dependencies` | 77 µs | 16 KB |
| `find_large_classes` | 77 µs | 1.2 KB |
| `find_reflection_usage` | 97 µs | 15 KB |
| `find_callers` | 211 µs | 38 KB |
| `search_symbols` | 603 µs | 2.3 KB |
| `find_references` | 1.1 ms | 208 KB |
| `find_unused_symbols` | 1.3 ms | 212 KB |
| `find_naming_violations` | 39 ms | 671 KB |
| Solution loading (one-time) | ~1.1 s | 8 MB |

## Hot Reload

The server watches `.cs`, `.csproj`, `.props`, and `.targets` files for changes. When a change is detected, affected projects are lazily re-compiled on the next tool query — only stale projects and their downstream dependents are re-compiled, not the full solution.

Location-returning tools include an `IsGenerated` flag to distinguish source-generator output from hand-written code.

## Requirements

- .NET 10 SDK
- A .NET solution with compilable projects

## Development

```bash
dotnet build
dotnet test
dotnet run --project benchmarks/RoslynCodeGraph.Benchmarks -c Release
```

## License

MIT
