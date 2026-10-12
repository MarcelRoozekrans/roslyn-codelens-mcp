# Roslyn CodeLens Plugin

## Prerequisites

Before installing this plugin, ensure you have:

- **.NET 10 SDK** on `PATH` — [Download](https://dotnet.microsoft.com/download)

No separate tool install is needed: the plugin starts the server with
`dnx --yes --version <plugin version> RoslynCodeLens.Mcp`, which downloads the matching
`RoslynCodeLens.Mcp` package on first use and runs it from the NuGet cache afterwards.

## Install

```bash
claude install gh:MarcelRoozekrans/roslyn-codelens-mcp
```
