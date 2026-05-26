# Structured Tool Errors + Cancellation Pass-Through — Design

**Date:** 2026-05-26
**Status:** Approved, ready for implementation plan

**Motivation:** Two related defects in the MCP server's contract with LLM consumers:

1. **Error shape is inconsistent.** Some tools throw (`LoadSolution`, `SetActiveSolution`, the analyzer-trust path in `GetDiagnostics`), some return bespoke `Success: bool` types (`apply_code_action`, `find_async_violations`), some return nullable (`analyze_method`, `get_type_overview`). When an LLM hits an error, it has no predictable `code` to switch on — it's parsing exception messages or remembering per-tool conventions.

2. **Cancellation is half-wired.** The MCP framework injects `CancellationToken` into async tool signatures, but only 8 of ~24 async Logic implementations thread it through to Roslyn. Long-running calls (`find_references`, `find_unused_symbols`, `get_diagnostics --includeAnalyzers`) can't be cancelled mid-flight.

## Goals

- Give LLM consumers a **prescribed error-code enum** so they can switch on `error.code` instead of parsing free-form messages.
- Make every async tool **actually honor** the `CancellationToken` it already accepts.
- Keep the change minimal: don't churn the success path of any tool that already works.

## Non-goals

- No `ToolResult<T>` universal envelope. The existing `Success: bool` result types (`CodeActionResult`, `FindAsyncViolationsResult`, etc.) stay — they encode tool-specific "ran but reports failure" semantics that don't fit a generic error envelope.
- No `CancellationToken` parameter on sync tools. They can't be cancelled mid-computation without thread interrupts; adding the param would be performative.
- No retry / backoff policies. That's client-side concern.
- No converting `OperationCanceledException` to a structured error. MCP has well-defined cancellation semantics; let the framework handle it.

## Architecture

### Two new types

```csharp
// src/RoslynCodeLens/Models/ToolErrorCode.cs
namespace RoslynCodeLens.Models;

public enum ToolErrorCode
{
    SymbolNotFound,
    SolutionNotTrusted,
    AmbiguousMatch,
    FileNotFound,
    ProjectNotFound,
    InvalidArgument,
    Internal,
}
```

```csharp
// src/RoslynCodeLens/McpToolException.cs
namespace RoslynCodeLens;

public sealed class McpToolException : Exception
{
    public ToolErrorCode Code { get; }
    public object? Details { get; }

    public McpToolException(ToolErrorCode code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }
}
```

### Exception filter in the host

`Program.cs` registers an exception-to-content filter on the MCP server host. When a tool invocation throws:

- `McpToolException` → emit `{ isError: true, content: [{ type: "text", text: <JSON{code,message,details}> }] }`
- `OperationCanceledException` → propagate unchanged (framework handles cancellation natively)
- Any other `Exception` → emit `{ isError: true, content: [{ type: "text", text: <JSON{code:"Internal", message: ex.Message}> }] }`

The filter is a single integration point — no per-tool changes for surfacing. JSON serialization uses `System.Text.Json` with camelCase naming, matching the existing envelope shape.

### Cancellation flow

No tool signatures change. The MCP framework already injects `CancellationToken` into async `Execute(...)` methods that declare it. The work is auditing the **Logic layer**:

- Every async `ExecuteAsync` must take `CancellationToken` as its final parameter and pass it to every awaited Roslyn call.
- Hot loops (per-project, per-syntax-tree, per-analyzer) get `ct.ThrowIfCancellationRequested()` at the top of the iteration body.
- Sync tools that wrap async via `.GetAwaiter().GetResult()` stay sync; cancellation doesn't apply.

### What changes per tool

**Group A — Convert existing throws to `McpToolException`:**

| Tool / Logic | Today | New |
|---|---|---|
| `LoadSolution` | `throw new InvalidOperationException("file not found")` | `McpToolException(FileNotFound, ..., { path })` |
| `SetActiveSolution` (ambiguous) | `throw new InvalidOperationException("ambiguous")` | `McpToolException(AmbiguousMatch, ..., { matches: [...] })` |
| `SetActiveSolution` (no match) | throw | `McpToolException(ProjectNotFound, ...)` |
| `UnloadSolution` (no match) | throw | `McpToolException(ProjectNotFound, ...)` |
| `GetDiagnosticsLogic` (untrusted+analyzers) | `throw new InvalidOperationException("not trusted")` | `McpToolException(SolutionNotTrusted, ..., { solutionPath })` |
| `GetCodeFixesLogic` (untrusted+analyzers) | same | same |
| `MultiSolutionManager.EnsureLoaded` (no solution) | `throw new InvalidOperationException` | `McpToolException(InvalidArgument, "no solution loaded")` |

**Group B — Replace nullable-as-error with `McpToolException`:**

| Tool | Today | New |
|---|---|---|
| `analyze_method` | returns `MethodAnalysis?` (null = not found) | throws `SymbolNotFound`; non-nullable return |
| `get_type_overview` | returns `TypeOverview?` | throws `SymbolNotFound` |
| `get_type_hierarchy` | returns `TypeHierarchy?` | throws `SymbolNotFound` |
| `get_symbol_context` | returns `SymbolContext?` | throws `SymbolNotFound` |
| `get_file_overview` | returns `FileOverview?` (null = file not in solution) | throws `FileNotFound` |

**Group C — Cancellation pass-through audit:**

These tools have typical latency > 100 ms per the README benchmark; honoring CT matters here.

- `find_references` · `find_callers` · `find_tests_for_symbol`
- `find_unused_symbols` (loops over `resolver.AllTypes`)
- `analyze_change_impact` (delegates to find_references + find_callers)
- `find_naming_violations` · `find_async_violations` · `find_disposable_misuse`
- `get_diagnostics` with `includeAnalyzers: true` (the `AnalyzerRunner.RunAnalyzersAsync` per-analyzer loop is the hot path)
- `rebuild_solution`
- `peek_il` (IL decompilation on huge methods can take seconds)

For each: ensure the `CancellationToken` parameter exists on `ExecuteAsync`, is passed to every awaited Roslyn API, and at least one `ct.ThrowIfCancellationRequested()` sits at the top of the outermost hot loop.

**Group D — Host wiring:**

- `Program.cs` registers the `McpToolException` filter on the server-host pipeline.
- One new test class (`McpToolExceptionFilterTests`) verifies the JSON shape of an error response end-to-end (using a minimal in-process MCP transport mock).

## Migration ordering

Single PR, multiple commits:

1. Foundations — `ToolErrorCode`, `McpToolException`, filter wiring, filter test. (1 commit. No tool changes; all green.)
2. Group A — convert existing throws. ~3-4 commits (cluster by file).
3. Group B — nullable → throw. 1 commit. **This is the only breaking behavior change in the PR** (callers expecting null now get an exception).
4. Group C — cancellation honoring. ~3-4 commits (one per tool family).
5. Docs catch-up: README & SKILL.md note on error codes + breaking-change callout for nullable returns. 1 commit.

Total: ~10 commits, single PR titled `feat: structured tool errors + cancellation pass-through`.

## Test strategy

- **Foundations test:** `McpToolExceptionFilterTests` — assert the filter produces the documented JSON content for a thrown `McpToolException`, an `OperationCanceledException`, and a generic `Exception`.
- **Per-tool error tests:** for each Group A and Group B tool, add (or update) one test that asserts the right `ToolErrorCode` is raised on the error path. Most can extend existing `*ToolTests.cs` files.
- **Cancellation tests:** for Group C, add a test that pre-cancels a token, invokes the Logic method, and asserts `OperationCanceledException`. Don't test mid-flight cancellation — too race-prone for unit tests.

## Risks and accepted trade-offs

- **Breaking change for callers that depended on nullable returns.** Acceptable: there are no production consumers other than Claude, and the new throw with a specific code is strictly more informative than `null`. Noted in the PR body.
- **Error-code enum is closed-set.** Adding a new code requires a code change to the enum. Acceptable — that's the whole point of giving LLMs a switchable surface. If a tool needs to communicate something not in the enum, that's a signal the enum should grow, not that the tool should invent a free-form string.
- **The filter introduces one global try-catch around tool calls.** That's already how the MCP framework handles thrown exceptions; we're just replacing the default `ex.Message` projection with a structured projection for our exception type. No new failure modes.

## Out of scope (intentional)

- Logging / telemetry hooks (#7 from the enhancement audit) — separate work.
- Progress callbacks for long ops (#4) — separate work.
- Stale-tolerance parameter (#8) — separate work.
- Bespoke `Success: bool` result types — they stay as-is.
- Sync-tool cancellation — sync stays sync.
