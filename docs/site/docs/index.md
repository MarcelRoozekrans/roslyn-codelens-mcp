---
title: Overview
slug: /
---

# roslyn-codelens-mcp

**roslyn-codelens-mcp** is a [Model Context Protocol](https://modelcontextprotocol.io) server that gives AI agents deep semantic understanding of .NET codebases — without needing to grep source files or know where symbols appear in the editor.

## Why symbol names instead of coordinates?

Most code intelligence tools require `(filePath, line, column)` — you need to already know where a symbol lives. `roslyn-codelens-mcp` uses **symbol names** (`"IGreeter"`, `"Greeter.Greet"`). The server resolves them through a pre-built index, so tools work without an open editor.

## What it does

- **Navigate** — go to definitions, find references, callers, implementations, attribute usages
- **Analyse** — type hierarchies, method flow, data flow, change impact
- **Diagnose & refactor** — diagnostics, code fixes, code actions with diff preview
- **Inspect quality** — unused symbols, complexity, naming violations, circular dependencies
- **Understand DI** — find registrations, lifetimes, constructor wiring
- **Inspect assemblies** — browse NuGet package APIs, peek IL disassembly

## Quick start

```bash
dotnet tool install -g RoslynCodeLens.Mcp
```

Then add to your `.mcp.json`:

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "roslyn-codelens-mcp",
      "args": ["--solution", "/path/to/your/Solution.sln"]
    }
  }
}
```

See [Installation](getting-started/installation) for full setup details.

## Working with results

List-returning tools (everything that produces "all X" or "every Y") cap their items at a per-tool default and wrap them in an envelope:

```json
{ "items": [ ... ], "totalCount": 142, "truncated": false, "limit": 500, "summary": { ... } }
```

If `truncated` is `true`, `items` is the top N by the tool's natural sort order (errors first, worst-first, most-relevant-first). Raise `limit` only if the tail matters for what you're doing — usually it won't.
