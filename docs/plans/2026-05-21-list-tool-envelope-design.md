# List-Tool Response Envelope — Design

**Date:** 2026-05-21
**Status:** Approved, ready for implementation plan
**Motivation:** Reduce LLM token usage on large result sets and prevent context blow-ups by adding a consistent envelope (truncation + optional aggregate summary) to every list-returning MCP tool. Pattern adapted from `jkolo/debug-mcp` (reimplemented; their license is AGPL-3.0).

## Goals

- Bound the size of any single list-tool response by default, so a runaway result set can't fill the model's context.
- Surface aggregate signal up front (severity counts, by-project breakdown, etc.) so the model can decide whether to drill into the items list.
- Uniform shape across all ~25 list-returning tools — one mental model in `SKILL.md`, not 25.

## Non-goals

- Single-object tools (`GetTypeOverview`, `GetSymbolContext`, `ApplyCodeAction`, `GoToDefinition` on a unique target, etc.) keep their current shape.
- No pagination cursors. `limit` is a single-shot cap; if a caller wants more, they raise `limit`.
- No backwards-compatibility shim. Pre-1.0, Claude is the only consumer, breaking the response shape is acceptable.

## Envelope type

```csharp
// Models/ToolListResult.cs
public record ToolListResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    bool Truncated,
    int Limit,
    object? Summary = null);

public static class ToolListResult
{
    public static ToolListResult<T> Create<T>(
        IReadOnlyList<T> items,
        int limit,
        object? summary = null)
    {
        var truncated = items.Count > limit;
        var sliced = truncated ? items.Take(limit).ToList() : items;
        return new ToolListResult<T>(sliced, items.Count, truncated, limit, summary);
    }
}
```

- `Summary` is intentionally typed as `object?` so each tool emits an anonymous shape suited to its data. JSON serialization handles it; the LLM reads it as a free-form object.
- Slicing happens at the Tool wrapper layer. `*Logic.cs` keeps returning `IReadOnlyList<T>` so it remains a pure list producer (easy to unit-test, no double-truncation).

## Per-tool defaults

Each tool has a tuned default limit and a defined sort order. Sort order is part of the contract because "top N" only means something useful if the most relevant items come first.

| Tool | Default limit | Sort | Summary |
|---|---|---|---|
| `get_diagnostics` | 1000 | severity desc, file, line | `{ error, warning, info, hidden }` |
| `find_references` | 500 | file, line | `{ byProject: { name: count } }` |
| `find_callers` | 500 | file, line | `{ byProject: { name: count } }` |
| `find_implementations` | 200 | file, line | none |
| `search_symbols` | 200 | match quality (exact → prefix → substring) | `{ byKind: { Class, Method, ... } }` |
| `goto_definition` | 50 | file, line | none |
| `find_attribute_usages` | 500 | file, line | `{ byProject: ... }` |
| `find_event_subscribers` | 500 | file, line | none |
| `find_reflection_usage` | 500 | severity desc, file | `{ byKind: { GetType, Invoke, ... } }` |
| `find_unused_symbols` | 500 | project, file | `{ byKind: { Class, Method, Field, ... } }` |
| `find_naming_violations` | 500 | rule, file | `{ byRule: { ... } }` |
| `find_large_classes` | 100 | size desc | none |
| `find_circular_dependencies` | 100 | cycle length desc | none |
| `get_complexity_metrics` | 100 | complexity desc | `{ max, avg, overThreshold }` |
| `get_di_registrations` | 200 | service name | none |
| `get_generated_code` | 200 | project, file | none |
| `get_source_generators` | 100 | project | none |
| `list_solutions` | 50 | name | none |

Worst-first tools (large classes, complexity, circular deps) — truncating top N gives the actionable subset; the long tail is rarely useful.

Severity-first tools (diagnostics, reflection usage) — errors before warnings so the truncated set keeps the important items.

## Tool-signature changes

Each Tool layer signature changes from:

```csharp
public static IReadOnlyList<T> Execute(...)
```

to:

```csharp
public static ToolListResult<T> Execute(..., int? limit = null)
```

with a per-tool default applied when `limit` is null. The `limit` parameter is declared with `[Description("Maximum number of items to return (default: <N>)")]` so it appears in the MCP tool schema.

## Migration order

Five PRs, each green before the next:

1. **Foundations** — `Models/ToolListResult.cs` + helper + unit tests for the helper (empty, below, at, above limit; null/non-null summary passthrough).
2. **Diagnostics pilot** — convert `get_diagnostics` only. Validate the envelope end-to-end through MCP transport. Update its tests. Confirm SKILL.md still reads correctly.
3. **High-volume finds** — `find_references`, `find_callers`, `find_implementations`, `search_symbols`, `goto_definition`, `find_attribute_usages`, `find_event_subscribers`, `find_reflection_usage`.
4. **Quality/metrics** — `find_unused_symbols`, `find_naming_violations`, `find_large_classes`, `find_circular_dependencies`, `get_complexity_metrics`.
5. **Misc** — `get_di_registrations`, `get_generated_code`, `get_source_generators`, `list_solutions`.

## Test strategy

Existing per-tool tests get updated mechanically to assert on `.Items` instead of the bare list.

Per-tool tests add:

- below-limit → `Truncated == false`, `Items.Count == TotalCount`
- exactly at-limit → `Truncated == false`
- above-limit → `Truncated == true`, `Items.Count == Limit`, `TotalCount > Limit`
- explicit `limit` override is respected
- for summary-producing tools, summary content matches against a fixed fixture

One integration test per summary-producing tool asserts the summary aggregate is correct end-to-end.

## SKILL.md update

A single new "Response shape" section near the top of [SKILL.md](../../plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md):

> All list-returning tools wrap results in: `{ items, totalCount, truncated, limit, summary? }`. When `truncated: true`, the items are the **top N by the tool's natural sort order** (severity-first, worst-first, etc.) — usually that's what you want. Raise `limit` only if the missing tail items matter.

No per-tool description churn — the envelope is uniform enough that existing descriptions still apply.

## Risks and accepted trade-offs

- **Breaking change to every list-tool response shape.** Accepted — pre-1.0, Claude is the only consumer.
- **Per-tool sort order is now part of the contract.** Worth a short comment near the final sort in each `*Logic.cs`.
- **Hard-coded per-tool defaults.** Not configurable at runtime. If a default turns out to be wrong, change the constant — don't add a config system.

## Out of scope (intentionally)

- Pagination / cursors
- `format: "envelope" | "list"` toggle for backwards compatibility
- Filter-echo in the envelope (Claude already has its call args in context)
- Restructuring single-object tool responses
