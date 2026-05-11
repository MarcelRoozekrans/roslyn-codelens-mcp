# Security Policy

## Reporting a Vulnerability

Use [GitHub Security Advisories](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/security/advisories/new) — please do not open public issues for security reports.

## Threat Model

This MCP server loads .NET solutions and exposes them to an AI assistant. Two operations execute code from the target solution as a side effect of inspection:

1. **Roslyn analyzers** (DLLs referenced via `<Analyzer Include="..." />` in `.csproj`). These run inside the MCP server process when `get_diagnostics` is called with `includeAnalyzers=true`, or when `get_code_fixes` is called for any diagnostic.
2. **Source generators** (DLLs referenced as analyzers, executed during `Compilation` creation). These run any time a project is compiled — i.e., on every tool invocation that requests a `Compilation`.

Both are arbitrary managed code with the privileges of the user running the MCP server.

## Trust Model

To mitigate untrusted analyzer execution ([GHSA-552p-8f74-6x7q](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/security/advisories/GHSA-552p-8f74-6x7q)):

### Solution trust

- Solutions passed on the CLI at server startup are **session-trusted** automatically (in-memory; lost on restart).
- Other solutions are **untrusted** by default. `get_diagnostics` will refuse `includeAnalyzers=true` for untrusted solutions and instruct the AI to call `trust_solution` after asking the user. `get_code_fixes` will refuse outright (it always needs analyzers to discover fixes).
- Trust scopes: `session` (in-memory), `persistent` (saved to `%APPDATA%\roslyn-codelens\trust.json`), `addRoot` (trust a directory prefix).
- Manage trust state with the `trust_solution`, `list_trusted_paths`, and `revoke_trust` MCP tools.

### Analyzer allowlist

Even for trusted solutions, only analyzer DLLs from known-safe locations are loaded. Default policy `nuget-and-solution-bin` accepts:

- `<userprofile>\.nuget\packages\**` — packages installed by NuGet restore
- `<dotnet-sdk-root>\**` — analyzers shipped with the .NET SDK
- `<solution-dir>\**\bin\**` and `<solution-dir>\**\obj\**` — build output of the solution itself

Solution-local analyzer DLLs *outside* `bin`/`obj` (e.g., `tools/EvilAnalyzer.dll` checked into a repo) are skipped even under a trusted solution.

Stricter alternative: `strict` (NuGet + SDK only — solution-bin denied). Opt-out: `all` (legacy behavior, equivalent to pre-mitigation). Set via the `analyzerPolicy` field in `trust.json`.

### Source generators

**Not yet gated.** Source generators are loaded by Roslyn at `Compilation` creation, before any tool runs. See the issue tracker for follow-up work.

## Known Limitations

- **No hash pinning** — a poisoned NuGet cache can still inject analyzers. The path allowlist accepts any DLL under the user's NuGet packages folder.
- **No symlink resolution** — an attacker who can place a symlink inside the NuGet packages folder pointing to a malicious DLL would bypass the path check.
- **Source generator execution is not gated** by this trust model. Planned for a follow-up.
- **Analyzer execution is in-process, not sandboxed.** A future advisory may move it to a child process.
- **`NUGET_PACKAGES` env var and `nuget.config` `globalPackagesFolder` are not honored** — the allowlist hardcodes `%USERPROFILE%\.nuget\packages`. Legitimate analyzers under a relocated cache will be rejected.
