# Roslyn CodeLens MCP

This is a Roslyn-based MCP server that provides semantic code intelligence for .NET codebases.

## MCP Server

The `.mcp.json` configures the roslyn-codelens MCP server to analyze this solution. It runs via `dotnet run` and connects over stdio.

## Skill

The `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` skill teaches Claude when and how to use the 22 code intelligence tools. Use the MCP tools instead of Grep/Glob for any .NET semantic queries (finding implementations, callers, references, diagnostics, etc.).

## Project Structure

- `src/RoslynCodeLens/` — MCP server (entry point: Program.cs)
- `tests/RoslynCodeLens.Tests/` — Unit tests (xUnit)
- `benchmarks/RoslynCodeLens.Benchmarks/` — BenchmarkDotNet performance tests
- `plugins/roslyn-codelens/` — Claude Code skill definition

## Development

- Build: `dotnet build`
- Test: `dotnet test`
- Benchmarks: `dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release`
