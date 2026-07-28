---
title: Installation
sidebar_position: 1
---

# Installation

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- A .NET solution (`.sln` or `.slnx` file)

## Install the tool

```bash
dotnet tool install -g RoslynCodeLens.Mcp
```

Verify the install:

```bash
roslyn-codelens-mcp --version
```

## Configure `.mcp.json`

Add the server to your project's `.mcp.json` (or `~/.claude/.mcp.json` for global config):

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "roslyn-codelens-mcp",
      "args": ["/absolute/path/to/YourSolution.sln"]
    }
  }
}
```

Solution paths are passed as bare arguments — there is no `--solution` flag. Pass
several to load them all at once and switch with `set_active_solution`:

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "roslyn-codelens-mcp",
      "args": [
        "/absolute/path/to/First.slnx",
        "/absolute/path/to/Second.slnx"
      ]
    }
  }
}
```

Arguments are optional. With none, the server searches the working directory for
a `.sln`/`.slnx`; if it finds nothing it still starts, prints a notice to stderr,
and tools return errors until you call `load_solution`.

## Verify the server starts

Restart your MCP client (Claude Code, etc.). The server loads the solution on startup — this takes 5–30 seconds for large solutions.

Once loaded, try:

```
Use get_type_overview to describe the type MyClass
```

If the server responds with type info, setup is complete.
