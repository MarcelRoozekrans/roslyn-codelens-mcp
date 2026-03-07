# Roslyn Code Graph MCP Server

A Roslyn-based MCP server that provides semantic code intelligence for .NET codebases. Designed for use with Claude Code to understand type hierarchies, call sites, DI registrations, and reflection usage.

## Features

- **find_implementations** — Find all classes/structs implementing an interface or extending a class
- **find_callers** — Find every call site for a method, property, or constructor
- **get_type_hierarchy** — Walk base classes, interfaces, and derived types
- **get_di_registrations** — Scan for DI service registrations
- **get_project_dependencies** — Get the project reference graph
- **get_symbol_context** — One-shot context dump for any type
- **find_reflection_usage** — Detect dynamic/reflection-based usage

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

All type lookups use pre-built reverse inheritance maps for O(1) access. Benchmarked on an i9-12900HK with .NET 10.0.3:

| Tool | Latency | Memory |
|------|--------:|-------:|
| `find_implementations` | 682 ns | 624 B |
| `find_callers` | 164 µs | 32 KB |
| `get_type_hierarchy` | 709 ns | 816 B |
| `get_symbol_context` | 1.3 µs | 1.0 KB |
| `get_di_registrations` | 55 µs | 13 KB |
| `get_project_dependencies` | 339 ns | 1.2 KB |
| `find_reflection_usage` | 87 µs | 15 KB |
| Solution loading (one-time) | ~1.1 s | 8 MB |

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
