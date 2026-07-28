# roslyn-codelens-mcp

npm launcher for [Roslyn CodeLens](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp) — a Roslyn-based MCP server that gives AI agents deep semantic understanding of .NET codebases.

```bash
npx -y roslyn-codelens-mcp
```

This package contains no server code. It is a thin shim that ensures the
[`RoslynCodeLens.Mcp`](https://www.nuget.org/packages/RoslynCodeLens.Mcp) .NET global tool is
installed at the matching version, then execs it. **The .NET 10 SDK must be on `PATH`.**

## MCP client config

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "roslyn-codelens-mcp"]
    }
  }
}
```

Solution paths can be passed as extra `args`; without them the server walks up from the working
directory to discover `.sln`/`.slnx` files.

If you already have the .NET SDK and prefer no npm indirection, install the tool directly:

```bash
dotnet tool install -g RoslynCodeLens.Mcp
```

See the [full documentation](https://marcelroozekrans.github.io/roslyn-codelens-mcp/) for the tool
catalog, runtime configuration, and the analyzer trust model.

MIT licensed.
