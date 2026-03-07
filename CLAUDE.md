# Roslyn Code Graph MCP

This is a Roslyn-based MCP server that provides semantic code intelligence for .NET codebases.

## MCP Server

The `.mcp.json` configures the roslyn-codegraph MCP server to analyze this solution. It runs via `dotnet run` and connects over stdio.

## Skill

The `plugins/roslyn-codegraph/skills/roslyn-codegraph/SKILL.md` skill teaches Claude when and how to use the 21 code intelligence tools. Use the MCP tools instead of Grep/Glob for any .NET semantic queries (finding implementations, callers, references, diagnostics, etc.).

## Project Structure

- `src/RoslynCodeGraph/` — MCP server (entry point: Program.cs)
- `tests/RoslynCodeGraph.Tests/` — Unit tests (xUnit)
- `benchmarks/RoslynCodeGraph.Benchmarks/` — BenchmarkDotNet performance tests
- `plugins/roslyn-codegraph/` — Claude Code skill definition

## Development

- Build: `dotnet build`
- Test: `dotnet test`
- Benchmarks: `dotnet run --project benchmarks/RoslynCodeGraph.Benchmarks -c Release`
