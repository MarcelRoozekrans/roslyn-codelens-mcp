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
dotnet tool install -g roslyn-codegraph-mcp
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

## Requirements

- .NET 10 SDK
- A .NET solution with compilable projects

## Development

```bash
dotnet build
dotnet test
```

## License

MIT
