# List-Tool Response Envelope Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a uniform `ToolListResult<T>` envelope (items + totalCount + truncated + limit + optional summary) to every list-returning MCP tool, with per-tool sort orders and tuned default limits.

**Architecture:** A single generic `ToolListResult<T>` record in `Models/`, populated by a `ToolListResult.Create()` helper. Each tool's `*Logic.cs` continues to return `IReadOnlyList<T>` (pure list producer, easy to test); the `*Tool.cs` wrapper applies the limit and builds the envelope. Sort order is now part of each logic's contract.

**Tech Stack:** C# / .NET 10, xUnit, MSBuildWorkspace, ModelContextProtocol.Server.

**Design doc:** `docs/plans/2026-05-21-list-tool-envelope-design.md`

---

## Conventions for every task

- **Test first.** Write the failing assertion, run it, see it fail, then implement.
- **Run:** `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~<TestClass>"` to scope.
- **Full suite before commit at end of each PR group:** `dotnet test`.
- **Commit per tool conversion** with message `feat(envelope): wrap <tool_name> in ToolListResult<T>`.
- **Don't touch SKILL.md until Task 21** — one consolidated edit at the end.

---

## PR Group 1 — Foundations

### Task 1: Add `ToolListResult<T>` record + helper

**Files:**
- Create: `src/RoslynCodeLens/Models/ToolListResult.cs`
- Create: `tests/RoslynCodeLens.Tests/Models/ToolListResultTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/RoslynCodeLens.Tests/Models/ToolListResultTests.cs
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests.Models;

public class ToolListResultTests
{
    [Fact]
    public void Create_EmptyList_ReturnsZeroCountNotTruncated()
    {
        var result = ToolListResult.Create<int>(Array.Empty<int>(), limit: 10);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.False(result.Truncated);
        Assert.Equal(10, result.Limit);
        Assert.Null(result.Summary);
    }

    [Fact]
    public void Create_BelowLimit_NotTruncated()
    {
        var result = ToolListResult.Create(new[] { 1, 2, 3 }, limit: 10);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(3, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Create_ExactlyAtLimit_NotTruncated()
    {
        var result = ToolListResult.Create(new[] { 1, 2, 3 }, limit: 3);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(3, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Create_AboveLimit_Truncated()
    {
        var result = ToolListResult.Create(new[] { 1, 2, 3, 4, 5 }, limit: 2);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new[] { 1, 2 }, result.Items);
        Assert.Equal(5, result.TotalCount);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.Limit);
    }

    [Fact]
    public void Create_PreservesSummary()
    {
        var summary = new { error = 3, warning = 7 };
        var result = ToolListResult.Create(new[] { 1, 2 }, limit: 10, summary);
        Assert.Same(summary, result.Summary);
    }
}
```

**Step 2: Run to verify failure**

`dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ToolListResultTests"`
Expected: compile error (type doesn't exist yet).

**Step 3: Implement**

```csharp
// src/RoslynCodeLens/Models/ToolListResult.cs
namespace RoslynCodeLens.Models;

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
        var sliced = truncated
            ? (IReadOnlyList<T>)items.Take(limit).ToList()
            : items;
        return new ToolListResult<T>(sliced, items.Count, truncated, limit, summary);
    }
}
```

**Step 4: Run tests, expect PASS**

**Step 5: Commit**

```
git add src/RoslynCodeLens/Models/ToolListResult.cs tests/RoslynCodeLens.Tests/Models/ToolListResultTests.cs
git commit -m "feat(envelope): add ToolListResult<T> record + helper"
```

---

## PR Group 2 — Diagnostics Pilot

### Task 2: Convert `get_diagnostics` to envelope

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetDiagnosticsTool.cs`
- Modify: `tests/RoslynCodeLens.Tests/Tools/GetDiagnosticsToolTests.cs`

Logic file is unchanged (keeps returning `IReadOnlyList<DiagnosticInfo>`). Only the Tool wrapper changes.

**Step 1: Write new failing tests (add to existing test class)**

```csharp
[Fact]
public async Task GetDiagnostics_Envelope_IncludesSeveritySummary()
{
    using var tempFile = new TempTrustFile();
    var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
    var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin",
        RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

    var manager = TestManagerFactory.WithLoaded(_loaded, _resolver);
    var result = await GetDiagnosticsTool.Execute(manager, trustStore, allowlist,
        project: null, severity: null, includeAnalyzers: false, limit: null, ct: default);

    Assert.NotNull(result);
    Assert.Equal(result.Items.Count, result.TotalCount); // not truncated by default in a clean fixture
    Assert.False(result.Truncated);
    Assert.Equal(1000, result.Limit);
    Assert.NotNull(result.Summary);

    // Summary must expose error/warning/info/hidden counts
    var summaryJson = System.Text.Json.JsonSerializer.Serialize(result.Summary);
    Assert.Contains("error", summaryJson, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("warning", summaryJson, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task GetDiagnostics_LimitOverride_Truncates()
{
    using var tempFile = new TempTrustFile();
    var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
    var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin",
        RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

    var manager = TestManagerFactory.WithLoaded(_loaded, _resolver);
    var result = await GetDiagnosticsTool.Execute(manager, trustStore, allowlist,
        project: null, severity: null, includeAnalyzers: false, limit: 1, ct: default);

    Assert.True(result.Items.Count <= 1);
    if (result.TotalCount > 1)
        Assert.True(result.Truncated);
    Assert.Equal(1, result.Limit);
}
```

Existing tests that called `GetDiagnosticsLogic.Execute(...)` keep working — they target Logic, not Tool. No churn there.

If `TestManagerFactory` doesn't exist, create it as a thin helper:
```csharp
// tests/RoslynCodeLens.Tests/Fixtures/TestManagerFactory.cs
public static class TestManagerFactory
{
    public static MultiSolutionManager WithLoaded(LoadedSolution loaded, SymbolResolver resolver)
    {
        // Construct or reuse however the existing test fixtures do — mirror SolutionManagerTests.
    }
}
```
If a simpler hook exists in the codebase (an `IMultiSolutionManager` interface or test-only constructor), prefer that. Investigate `MultiSolutionManager` usage in existing Tool tests before adding the factory.

**Step 2: Run tests, see failure**

`dotnet test --filter "FullyQualifiedName~GetDiagnosticsToolTests"`
Expected: compile errors on the new test (Tool signature still returns `IReadOnlyList<DiagnosticInfo>`).

**Step 3: Update tool wrapper**

```csharp
// src/RoslynCodeLens/Tools/GetDiagnosticsTool.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetDiagnosticsTool
{
    private const int DefaultLimit = 1000;

    [McpServerTool(Name = "get_diagnostics"),
     Description("List compiler errors and warnings across the solution, optionally including analyzer diagnostics. " +
                 "Analyzer diagnostics require the solution to be trusted (see 'trust_solution'). " +
                 "Returns an envelope with items, totalCount, truncated, limit, and a severity summary.")]
    public static async Task<ToolListResult<DiagnosticInfo>> Execute(
        MultiSolutionManager manager,
        Security.TrustStore trustStore,
        Security.AnalyzerAllowlist allowlist,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum severity: 'error' or 'warning' (default: warning)")] string? severity = null,
        [Description("Include analyzer diagnostics (default: false — requires trust_solution to be called first)")]
            bool includeAnalyzers = false,
        [Description("Maximum number of items to return (default: 1000). Items are sorted severity-desc, file, line.")]
            int? limit = null,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var raw = await GetDiagnosticsLogic.ExecuteAsync(
            manager.GetLoadedSolution(), manager.GetResolver(),
            project, severity, includeAnalyzers, trustStore, allowlist, ct).ConfigureAwait(false);

        var sorted = SortBySeverityFileLine(raw);
        var summary = BuildSummary(raw);
        return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
    }

    private static IReadOnlyList<DiagnosticInfo> SortBySeverityFileLine(IReadOnlyList<DiagnosticInfo> items)
    {
        // Severity rank: error=0, warning=1, info=2, hidden=3 (lower = more important)
        return items
            .OrderBy(d => SeverityRank(d.Severity))
            .ThenBy(d => d.File, StringComparer.Ordinal)
            .ThenBy(d => d.Line)
            .ToList();
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Error" => 0,
        "Warning" => 1,
        "Info" => 2,
        "Hidden" => 3,
        _ => 4,
    };

    private static object BuildSummary(IReadOnlyList<DiagnosticInfo> items)
    {
        var error = 0; var warning = 0; var info = 0; var hidden = 0;
        foreach (var d in items)
        {
            switch (d.Severity)
            {
                case "Error": error++; break;
                case "Warning": warning++; break;
                case "Info": info++; break;
                case "Hidden": hidden++; break;
            }
        }
        return new { error, warning, info, hidden };
    }
}
```

**Step 4: Run tests, expect PASS**

`dotnet test --filter "FullyQualifiedName~GetDiagnosticsToolTests"`

**Step 5: Sanity-build and run full test suite**

`dotnet build` then `dotnet test`. No other test should break.

**Step 6: Commit**

```
git commit -am "feat(envelope): wrap get_diagnostics in ToolListResult<T>"
```

---

## PR Group 3 — High-Volume Find Tools

Each task below follows the **same template** as Task 2:
1. Add envelope-shape tests (totalCount, truncated, limit, summary if applicable) to existing test file.
2. See them fail.
3. Update the Tool wrapper: add `int? limit = null` parameter, return `ToolListResult<T>`, apply sort, build summary (or pass null).
4. Update existing tests in that file that asserted on raw list — change `Assert.Empty(results)` → `Assert.Empty(results.Items)` etc. Most are 1-2 char edits.
5. Run scoped tests, then full suite. Commit.

The summary builders below are **complete code** — paste them into the Tool file as private static helpers.

### Task 3: `find_references` → envelope

**Default limit:** 500. **Sort:** file (Ordinal), then line. **Summary:** `{ byProject: Dictionary<string,int> }`.

```csharp
private static object BuildSummary(IReadOnlyList<SymbolReference> items)
{
    var byProject = items
        .GroupBy(r => r.Project, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    return new { byProject };
}

private static IReadOnlyList<SymbolReference> Sort(IReadOnlyList<SymbolReference> items)
    => items.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
```

Modify `FindReferencesTool.cs`, update `FindReferencesToolTests.cs`. Commit: `feat(envelope): wrap find_references in ToolListResult<T>`.

> **If `SymbolReference` has no `Project` property, check the actual model and either group by file (fallback) or skip the summary.** Verify before writing the summary code.

### Task 4: `find_callers` → envelope

**Default limit:** 500. **Sort:** file, line. **Summary:** `{ byProject }` (same pattern as Task 3, using `CallerInfo.Project` if present — otherwise omit summary).

### Task 5: `find_implementations` → envelope

**Default limit:** 200. **Sort:** file, line. **Summary:** `null`. Items are `SymbolLocation`.

### Task 6: `search_symbols` → envelope

**Default limit:** 200. **Sort:** match quality (exact → prefix → substring); within each bucket, alphabetical by name. **Summary:** `{ byKind: Dictionary<string,int> }` keyed by symbol kind (`Class`, `Method`, ...).

```csharp
private static IReadOnlyList<SymbolLocation> SortByMatchQuality(string query, IReadOnlyList<SymbolLocation> items)
{
    return items
        .OrderBy(s => MatchRank(s, query))
        .ThenBy(s => s.Symbol, StringComparer.Ordinal)
        .ToList();
}

private static int MatchRank(SymbolLocation s, string query)
{
    // Assume SymbolLocation has a Symbol or Name property; verify in source.
    var name = s.Symbol;
    if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return 0;
    if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
    return 2;
}
```

> Verify `SymbolLocation` field names before pasting. Adjust property accesses to match.

### Task 7: `goto_definition` → envelope

**Default limit:** 50. **Sort:** file, line. **Summary:** `null`. Typically 1-2 items; the limit is a safety cap.

### Task 8: `find_attribute_usages` → envelope

**Default limit:** 500. **Sort:** file, line. **Summary:** `{ byProject }` if `AttributeUsageInfo.Project` exists.

### Task 9: `find_event_subscribers` → envelope

**Default limit:** 500. **Sort:** file, line. **Summary:** `null`.

### Task 10: `find_reflection_usage` → envelope

**Default limit:** 500. **Sort:** severity desc (errors/warnings first if the model has a severity), then file. **Summary:** `{ byKind }` grouped by reflection-call type (e.g. `GetType`, `Invoke`, `GetMethod`) — check `ReflectionUsage` model for the relevant property name.

---

## PR Group 4 — Quality / Metrics Tools

Same template as Group 3.

### Task 11: `find_unused_symbols` → envelope

**Default limit:** 500. **Sort:** project, then file. **Summary:** `{ byKind: Dictionary<string,int> }` (Class, Method, Field, Property, Event).

### Task 12: `find_naming_violations` → envelope

**Default limit:** 500. **Sort:** rule id, then file. **Summary:** `{ byRule: Dictionary<string,int> }`.

### Task 13: `find_large_classes` → envelope

**Default limit:** 100. **Sort:** size desc (worst first — likely member count or line count; check `LargeClassInfo`). **Summary:** `null`.

### Task 14: `find_circular_dependencies` → envelope

**Default limit:** 100. **Sort:** cycle length desc. **Summary:** `null`.

### Task 15: `get_complexity_metrics` → envelope

**Default limit:** 100. **Sort:** complexity desc. **Summary:** `{ max, avg, overThreshold }`.

```csharp
private static object BuildSummary(IReadOnlyList<ComplexityMetric> items, int threshold)
{
    if (items.Count == 0) return new { max = 0, avg = 0.0, overThreshold = 0 };
    var max = items.Max(m => m.Complexity);    // verify property name
    var avg = items.Average(m => m.Complexity);
    var overThreshold = items.Count(m => m.Complexity > threshold);
    return new { max, avg, overThreshold };
}
```

> The `threshold` parameter already exists on this tool. Pass it through to `BuildSummary`.

---

## PR Group 5 — Miscellaneous List Tools

### Task 16: `get_di_registrations` → envelope

**Default limit:** 200. **Sort:** service name (Ordinal). **Summary:** `null`.

### Task 17: `get_generated_code` → envelope

**Default limit:** 200. **Sort:** project, then file. **Summary:** `null`.

### Task 18: `get_source_generators` → envelope

**Default limit:** 100. **Sort:** project (Ordinal). **Summary:** `null`.

### Task 19: `list_solutions` → envelope

**Default limit:** 50. **Sort:** name (Ordinal). **Summary:** `null`. Even though this list is small, conversion keeps the contract uniform.

---

## PR Group 6 — Cleanup

### Task 20: Audit remaining list-returning tools

**Step 1:** Run

```
grep -rn "IReadOnlyList<" src/RoslynCodeLens/Tools/ | grep -E "Tool\.cs:"
```

Verify only the converted tools and the explicitly out-of-scope ones (single-object returners) remain. **If any list-returning tool slipped through the plan**, convert it using the same template (200 default, file/line sort, null summary unless an aggregate is obviously useful).

**Step 2:** Run full test suite: `dotnet test`. Expect all green.

**Step 3:** Commit any straggler conversions individually.

---

### Task 21: Update SKILL.md

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`

**Step 1:** Add a "Response shape" section near the top (after the opening intro / before tool catalog):

```markdown
## Response shape

All list-returning tools wrap their results in an envelope:

```json
{
  "items": [...],           // the actual records
  "totalCount": 142,        // how many existed before truncation
  "truncated": false,       // true if items.length < totalCount
  "limit": 500,             // the cap that was applied
  "summary": { ... }        // optional aggregate; varies by tool
}
```

When `truncated` is `true`, the items are the **top N by the tool's natural sort order**
(severity-first, worst-first, alphabetical, etc.) — usually that's exactly what you want.
Raise `limit` only if the truncated tail items matter for the task.

Tools with summary aggregates today: `get_diagnostics` (severity counts),
`find_references` / `find_callers` / `find_attribute_usages` (by project),
`search_symbols` / `find_reflection_usage` / `find_unused_symbols` (by kind),
`find_naming_violations` (by rule), `get_complexity_metrics` (max/avg/overThreshold).
```

**Step 2:** Verify no other section in SKILL.md describes per-tool response shapes that would now be wrong.

**Step 3:** Commit.

```
git commit -am "docs(skill): document list-tool envelope response shape"
```

---

### Task 22: Final verification

**Step 1:** `dotnet build` — must succeed with no warnings introduced.

**Step 2:** `dotnet test` — full suite green.

**Step 3:** Manually smoke-test via the MCP server:
- Start the server (the existing `.mcp.json` works).
- Call `get_diagnostics` and visually confirm the envelope shape in the response.
- Call `find_references` on a popular symbol; confirm `summary.byProject` is populated.
- Call `get_complexity_metrics` with a low threshold; confirm `summary.overThreshold` is non-zero.

**Step 4:** If everything passes, the work is complete. If anything is off, fix and re-test before declaring done.

---

## Gotchas / things to watch for

- **Test classes use `_loaded` and `_resolver` fields from `TestSolutionFixture`.** Don't break the `[Collection("TestSolution")]` pattern.
- **The fixture intentionally allows some `CS0246` adapter-restore flakes** (see comment block at top of `GetDiagnosticsToolTests.cs`). Preserve that filter logic when you update the test — it's not unrelated noise.
- **`Logic.cs` files should remain pure list producers.** Don't push truncation into them — it belongs at the Tool boundary. Doing this twice would double-truncate.
- **Sort order is a contract.** When you write the sort, leave a one-line comment explaining why: `// Sorted severity-desc so truncated top-N keeps the most important diagnostics.`
- **Model field names may differ from what I guessed in this plan.** Always read the relevant `Models/*.cs` before pasting summary code. If a property doesn't exist, adapt or drop that summary.
- **`object?` summaries** — JSON serialization through MCP just works for anonymous types. Don't introduce typed summary classes unless one is reused across tools.
