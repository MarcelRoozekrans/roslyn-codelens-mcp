# Structured Tool Errors + Cancellation Pass-Through Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace ad-hoc throws and nullable-as-error returns across the tool surface with a structured `McpToolException` carrying a prescribed `ToolErrorCode` enum, surfaced to MCP clients as `{ isError: true, content: [{ type: "text", text: "{\"code\":\"…\",\"message\":\"…\",\"details\":…}" }] }`. Audit ~11 high-latency async tools to honor the `CancellationToken` the MCP framework already provides.

**Architecture:** Two new types in `RoslynCodeLens` (`ToolErrorCode` enum + `McpToolException`). One exception-conversion point at the MCP host boundary in `Program.cs` (mechanism TBD — see Task 2). All existing throws and five nullable-return tools are migrated to throw `McpToolException` with the right code. Async Logic methods get `CancellationToken` threaded end-to-end with `ThrowIfCancellationRequested` at hot-loop tops.

**Tech Stack:** C# / .NET 10, Roslyn `Microsoft.CodeAnalysis`, `ModelContextProtocol` 1.3.0 (stdio MCP server), xUnit.

**Design doc:** `docs/plans/2026-05-26-structured-tool-errors-design.md`

---

## Conventions

- TDD: failing test → red → implement → green → commit.
- Scoped runs: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~<Class>"`.
- Full suite before merge: `dotnet test`.
- Per-task commit, message prefix `feat(errors):` for code, `test(errors):` for tests-only.
- The documented `IsAdapterRestoreFlake` (NUnit/MSTest/XUnit fixture restore CS0103/CS0234/CS0246) is environmental — keep its filter logic intact when editing `GetDiagnosticsToolTests`.
- `InternalsVisibleTo("RoslynCodeLens.Tests")` is set, so `internal` types are reachable from the test project.

---

## Task 1: Add `ToolErrorCode` + `McpToolException` + unit tests

**Files:**
- Create: `src/RoslynCodeLens/Models/ToolErrorCode.cs`
- Create: `src/RoslynCodeLens/McpToolException.cs`
- Create: `tests/RoslynCodeLens.Tests/McpToolExceptionTests.cs`

### Step 1: Write the failing tests

```csharp
// tests/RoslynCodeLens.Tests/McpToolExceptionTests.cs
using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class McpToolExceptionTests
{
    [Fact]
    public void Ctor_SetsCodeAndMessage()
    {
        var ex = new McpToolException(ToolErrorCode.SymbolNotFound, "X");
        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
        Assert.Equal("X", ex.Message);
        Assert.Null(ex.Details);
    }

    [Fact]
    public void Ctor_PreservesDetails()
    {
        var details = new { path = "/foo/bar" };
        var ex = new McpToolException(ToolErrorCode.FileNotFound, "missing", details);
        Assert.Same(details, ex.Details);
    }

    [Fact]
    public void Enum_HasAllExpectedCodes()
    {
        // Lock the catalog. Adding a code is intentional; removing one is breaking.
        var expected = new[]
        {
            "SymbolNotFound", "SolutionNotTrusted", "AmbiguousMatch",
            "FileNotFound", "ProjectNotFound", "InvalidArgument", "Internal",
        };
        var actual = Enum.GetNames<ToolErrorCode>();
        Assert.Equal(expected.Length, actual.Length);
        foreach (var name in expected)
            Assert.Contains(name, actual, StringComparer.Ordinal);
    }
}
```

### Step 2: Run to verify failure

`dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~McpToolExceptionTests"`
Expected: compile error.

### Step 3: Implement

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
using RoslynCodeLens.Models;

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

### Step 4: Tests green

`dotnet test ... --filter "...McpToolExceptionTests"` → 3 pass.

### Step 5: Commit

```
git add src/RoslynCodeLens/Models/ToolErrorCode.cs src/RoslynCodeLens/McpToolException.cs tests/RoslynCodeLens.Tests/McpToolExceptionTests.cs
git commit -m "feat(errors): add McpToolException + ToolErrorCode enum"
```

---

## Task 2: Wire exception filter at the MCP host boundary

**Files:**
- Modify: `src/RoslynCodeLens/Program.cs`
- Possibly create: `src/RoslynCodeLens/McpToolExceptionFormatter.cs` (helper for JSON projection)
- Create: `tests/RoslynCodeLens.Tests/McpToolExceptionFormatterTests.cs`

### Background research (mandatory before writing code)

The `ModelContextProtocol` 1.3.0 NuGet package exposes the server-side via `IMcpServerBuilder` extension methods (`AddMcpServer`, `WithStdioServerTransport`, `WithToolsFromAssembly`). The exception-conversion mechanism for thrown tools is package-specific. **Before implementing, the engineer must:**

1. Inspect the installed `ModelContextProtocol.dll` (path: `~/.nuget/packages/modelcontextprotocol/1.3.0/lib/net10.0/`) using `inspect_external_assembly` MCP tool (yes, dogfooding) for any of:
   - A `IMcpServerToolFilter` / `IExceptionHandler` interface
   - An `McpServerOptions.OnException` callback
   - Middleware via `Use(...)`
   - Per-tool `OnError` attribute
2. If none of the above exists, the fallback is to **catch in the Tool wrappers** — but that's per-tool churn. Avoid this if at all possible.

Report back what was found before writing any code. The plan can then be refined to specify the exact wiring API.

### Step 1: Write the failing test on the projection logic

Regardless of wiring mechanism, the JSON projection is pure: input is an `Exception`, output is a string. Test that contract first:

```csharp
// tests/RoslynCodeLens.Tests/McpToolExceptionFormatterTests.cs
using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class McpToolExceptionFormatterTests
{
    [Fact]
    public void Format_McpToolException_ProducesStructuredJson()
    {
        var ex = new McpToolException(
            ToolErrorCode.SolutionNotTrusted,
            "Solution 'Foo.sln' is not trusted.",
            new { solutionPath = "C:\\Foo.sln" });

        var text = McpToolExceptionFormatter.FormatAsContentText(ex);
        Assert.Contains("\"code\":\"SolutionNotTrusted\"", text, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"Solution 'Foo.sln' is not trusted.\"", text, StringComparison.Ordinal);
        Assert.Contains("\"solutionPath\":\"C:\\\\Foo.sln\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_McpToolException_OmitsDetailsKeyWhenNull()
    {
        var ex = new McpToolException(ToolErrorCode.SymbolNotFound, "X");
        var text = McpToolExceptionFormatter.FormatAsContentText(ex);
        Assert.DoesNotContain("\"details\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OtherException_DefaultsToInternalCode()
    {
        var ex = new InvalidOperationException("boom");
        var text = McpToolExceptionFormatter.FormatAsContentText(ex);
        Assert.Contains("\"code\":\"Internal\"", text, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"boom\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OperationCanceledException_Throws()
    {
        // Cancellation must not be formatted — it should bubble unchanged for the MCP framework to handle.
        var ex = new OperationCanceledException();
        Assert.Throws<InvalidOperationException>(() => McpToolExceptionFormatter.FormatAsContentText(ex));
    }
}
```

### Step 2: Implement the formatter

```csharp
// src/RoslynCodeLens/McpToolExceptionFormatter.cs
using System.Text.Json;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

internal static class McpToolExceptionFormatter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FormatAsContentText(Exception ex)
    {
        if (ex is OperationCanceledException)
            throw new InvalidOperationException(
                "OperationCanceledException must propagate to the MCP framework, not be formatted.");

        var payload = ex switch
        {
            McpToolException mcp => new ErrorPayload(mcp.Code.ToString(), mcp.Message, mcp.Details),
            _ => new ErrorPayload(nameof(ToolErrorCode.Internal), ex.Message, null),
        };
        return JsonSerializer.Serialize(payload, s_options);
    }

    private sealed record ErrorPayload(string Code, string Message, object? Details);
}
```

> **Note:** `JsonNamingPolicy.CamelCase` lowercases the first letter of property names, so `Code` → `"code"`. The `ToolErrorCode` enum value is serialized as a string via `mcp.Code.ToString()` — gives `"SolutionNotTrusted"`, not `1`.

### Step 3: Tests green; commit foundations

```
git add src/RoslynCodeLens/McpToolExceptionFormatter.cs tests/RoslynCodeLens.Tests/McpToolExceptionFormatterTests.cs
git commit -m "feat(errors): add McpToolExceptionFormatter for structured error projection"
```

### Step 4: Wire the formatter into `Program.cs`

After the research in the "Background research" block above, write the exact wiring. Best case: a single line registering a filter:

```csharp
// Pseudo-code — replace with the actual API once research is done
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithExceptionFilter(McpToolExceptionFormatter.FormatAsContentText);
```

Worst case (no built-in hook): wrap `WithToolsFromAssembly()` with a custom decorator. **Do not** add try-catch to every `*Tool.cs` Execute method — that's the anti-pattern the design explicitly rejects.

### Step 5: Smoke-build + commit wiring

```
dotnet build src/RoslynCodeLens/RoslynCodeLens.csproj
git add src/RoslynCodeLens/Program.cs
git commit -m "feat(errors): wire McpToolException filter on server host"
```

---

## Task 3: Convert Group A throws — solution lifecycle tools

**Files:**
- Modify: `src/RoslynCodeLens/MultiSolutionManager.cs`
- Modify: `src/RoslynCodeLens/Tools/LoadSolutionTool.cs` (if it has any inline throws)

### Background

Audit lists 15 throw sites in `MultiSolutionManager.cs` and `SolutionManager.cs`. Not all are user-facing — some are internal invariants. Only convert throws that surface to MCP tool callers:

- `MultiSolutionManager.SetActive(...)` — ambiguous and not-found cases
- `MultiSolutionManager.Unload(...)` — not-found case
- `MultiSolutionManager.Load(...)` — error during load
- `MultiSolutionManager.Active` getter — "no solution loaded" case (read by every tool's `EnsureLoaded()`)

`SolutionManager`'s warmup-failed throws are infrastructure — leave them; they get re-thrown by `MultiSolutionManager` calls anyway and the message is already informative.

### Step 1: Find all surface-facing throw sites

```
grep -n "throw new InvalidOperationException" src/RoslynCodeLens/MultiSolutionManager.cs
```

Walk each. For each user-facing throw, plan the `McpToolException` replacement:

| Line | Today | New |
|---|---|---|
| ~64 | `"No solution loaded. Pass a .sln/.slnx path as argument."` or `"Active solution key 'X' not found"` | `McpToolException(InvalidArgument, ...)` |
| ~143 | (read context) likely no-match in SetActive | `McpToolException(ProjectNotFound, ...)` |
| ~147 | ambiguous match | `McpToolException(AmbiguousMatch, ..., details: { matches: [...] })` |
| ~177 | Load failure | `McpToolException(InvalidArgument, ...)` (the message describes the load failure) |
| ~216 / ~220 | Unload not-found | `McpToolException(ProjectNotFound, ...)` |

### Step 2: Write tests first

In `tests/RoslynCodeLens.Tests/MultiSolutionManagerTests.cs`, add tests like:

```csharp
[Fact]
public void SetActive_AmbiguousMatch_ThrowsAmbiguousMatchCode()
{
    var mgr = ... ; // existing fixture setup
    var ex = Assert.Throws<McpToolException>(() => mgr.SetActive("A")); // ambiguous prefix
    Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
    Assert.Contains("A", ex.Message, StringComparison.Ordinal);
}

[Fact]
public void SetActive_NoMatch_ThrowsProjectNotFoundCode()
{
    var mgr = ... ;
    var ex = Assert.Throws<McpToolException>(() => mgr.SetActive("nonexistent"));
    Assert.Equal(ToolErrorCode.ProjectNotFound, ex.Code);
}

[Fact]
public void EnsureLoaded_NoSolution_ThrowsInvalidArgumentCode()
{
    var mgr = MultiSolutionManager.CreateEmpty();
    var ex = Assert.Throws<McpToolException>(() => mgr.EnsureLoaded());
    Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
}
```

Run → fail (still throws `InvalidOperationException`).

### Step 3: Replace throws in `MultiSolutionManager.cs`

Pattern: `throw new InvalidOperationException("X")` → `throw new McpToolException(ToolErrorCode.Y, "X")`. Add `using RoslynCodeLens.Models;` if not present.

For ambiguous-match: include the matching keys in `details`:

```csharp
throw new McpToolException(
    ToolErrorCode.AmbiguousMatch,
    $"Multiple solutions match '{name}': {string.Join(", ", matches.Select(Path.GetFileName))}.",
    new { matches = matches.Select(m => new { path = m, name = Path.GetFileName(m) }).ToArray() });
```

### Step 4: Tests green

`dotnet test ... --filter "FullyQualifiedName~MultiSolutionManagerTests"`

### Step 5: Commit

```
git commit -am "feat(errors): convert MultiSolutionManager throws to McpToolException"
```

---

## Task 4: Convert Group A throws — analyzer trust path

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetDiagnosticsLogic.cs:31-36`
- Modify: `src/RoslynCodeLens/Tools/GetCodeFixesLogic.cs` (similar block)
- Modify: `tests/RoslynCodeLens.Tests/Tools/GetDiagnosticsToolTests.cs` — `GetDiagnostics_UntrustedSolution_ThrowsWhenAnalyzersRequested` test

### Step 1: Update the existing test to assert on the code

```csharp
[Fact]
public async Task GetDiagnostics_UntrustedSolution_ThrowsSolutionNotTrustedCode()
{
    using var tempFile = new TempTrustFile();
    var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
    var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist(
        "nuget-and-solution-bin",
        RoslynCodeLens.Security.AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);

    var ex = await Assert.ThrowsAsync<McpToolException>(async () =>
    {
        await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: true,
            trustStore, allowlist, CancellationToken.None);
    });

    Assert.Equal(ToolErrorCode.SolutionNotTrusted, ex.Code);
    Assert.Contains("not trusted", ex.Message, StringComparison.OrdinalIgnoreCase);
}
```

The existing `Assert.ThrowsAsync<InvalidOperationException>` test must be replaced (not duplicated).

### Step 2: Run, see failure

### Step 3: Update Logic

In `GetDiagnosticsLogic.cs:31-36`:

```csharp
if (solutionPath is null || !trustStore.IsTrusted(solutionPath))
{
    throw new McpToolException(
        ToolErrorCode.SolutionNotTrusted,
        $"Solution '{solutionPath ?? "<unknown>"}' is not trusted for analyzer execution. " +
        $"Analyzer DLLs run as in-process code, so the user must explicitly authorize them. " +
        $"Ask the user, then call the 'trust_solution' tool with this path. " +
        $"To get compiler-only diagnostics, retry with includeAnalyzers=false.",
        new { solutionPath });
}
```

Same change in `GetCodeFixesLogic.cs` for the matching trust-check block.

### Step 4: Tests green

`dotnet test ... --filter "FullyQualifiedName~GetDiagnosticsToolTests|FullyQualifiedName~GetCodeFixesToolTests"`

### Step 5: Commit

```
git commit -am "feat(errors): convert analyzer-trust throws to SolutionNotTrusted"
```

---

## Task 5: Convert Group A throws — find_breaking_changes baseline

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindBreakingChangesLogic.cs:140,148,162,183`
- Modify: `tests/RoslynCodeLens.Tests/Tools/FindBreakingChangesToolTests.cs`

### Audit

Four throws in `FindBreakingChangesLogic`:
- Line 140: baseline file not found → `FileNotFound`
- Line 148 / 162 / 183: read context first (JSON parse failure / invalid format / missing assembly) — most likely `InvalidArgument`. Verify by reading the surrounding code.

### Step 1: Add test for the FileNotFound path

```csharp
[Fact]
public void FindBreakingChanges_MissingBaseline_ThrowsFileNotFoundCode()
{
    var ex = Assert.Throws<McpToolException>(() =>
        FindBreakingChangesLogic.Execute(_loaded, _resolver, _metadata,
            baselinePath: "/nonexistent/baseline.json", project: null));
    Assert.Equal(ToolErrorCode.FileNotFound, ex.Code);
}
```

### Step 2-4: Replace, run, commit per the standard pattern

```
git commit -am "feat(errors): convert FindBreakingChanges baseline-load throws"
```

---

## Task 6: Convert Group A throws — generate_test_skeleton symbol-not-found

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs:20+`
- Modify: `tests/RoslynCodeLens.Tests/Tools/GenerateTestSkeletonToolTests.cs`

### Audit

`GenerateTestSkeletonLogic.cs:20`: `throw new InvalidOperationException($"Symbol not found: {symbol}")` → `McpToolException(SymbolNotFound, ..., new { symbol })`.

`Line 36`: read context — likely "framework not detected" or "type kind not supported". Probably `InvalidArgument`.

### Steps 1-5: Standard pattern.

```
git commit -am "feat(errors): convert GenerateTestSkeleton symbol-not-found throw"
```

---

## Task 7: Group B — convert nullable-as-error to throws (five tools)

**Files (one commit covers all five since the pattern is identical):**
- Modify: `src/RoslynCodeLens/Tools/AnalyzeMethodTool.cs`
- Modify: `src/RoslynCodeLens/Tools/AnalyzeMethodLogic.cs`
- Modify: `src/RoslynCodeLens/Tools/GetFileOverviewTool.cs`
- Modify: `src/RoslynCodeLens/Tools/GetFileOverviewLogic.cs`
- Modify: `src/RoslynCodeLens/Tools/GetSymbolContextTool.cs`
- Modify: `src/RoslynCodeLens/Tools/GetSymbolContextLogic.cs`
- Modify: `src/RoslynCodeLens/Tools/GetTypeHierarchyTool.cs`
- Modify: `src/RoslynCodeLens/Tools/GetTypeHierarchyLogic.cs`
- Modify: `src/RoslynCodeLens/Tools/GetTypeOverviewTool.cs`
- Modify: `src/RoslynCodeLens/Tools/GetTypeOverviewLogic.cs`
- Modify: corresponding `*ToolTests.cs` files (5 of them)

### Step 1: For each tool, find the test asserting on null and rewrite it

Example (`GetTypeOverviewToolTests`):

```csharp
[Fact]
public void GetTypeOverview_UnknownSymbol_ThrowsSymbolNotFoundCode()
{
    var ex = Assert.Throws<McpToolException>(() =>
        GetTypeOverviewLogic.Execute(_loaded, _resolver, _metadata, "Nonexistent.Type"));
    Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
}
```

If an existing test asserts `Assert.Null(result)` for the unknown-symbol case, replace it with the throw-pattern.

### Step 2: Change signatures

For each tool, change the Logic and Tool signatures from `T?` to `T` (non-nullable). Where the old code did `return null;`, change to:

```csharp
throw new McpToolException(
    ToolErrorCode.SymbolNotFound,
    $"Symbol '{symbol}' not found in solution.",
    new { symbol });
```

For `GetFileOverview` use `ToolErrorCode.FileNotFound` and pass `new { filePath = path }`.

### Step 3: Build will fail at any caller dereferencing the old nullable

```
dotnet build src/RoslynCodeLens/
```

Walk every compile error. Most are inside `tests/` and update naturally. If a Logic function calls another nullable-returning Logic, update the caller too.

### Step 4: Run scoped + full tests

```
dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~AnalyzeMethodToolTests|FullyQualifiedName~GetFileOverviewToolTests|FullyQualifiedName~GetSymbolContextToolTests|FullyQualifiedName~GetTypeHierarchyToolTests|FullyQualifiedName~GetTypeOverviewToolTests"
```

### Step 5: Commit

```
git commit -am "feat(errors): nullable-as-error returns now throw SymbolNotFound/FileNotFound

BREAKING: callers of analyze_method / get_file_overview / get_symbol_context /
get_type_hierarchy / get_type_overview that previously received null for
unknown symbols now receive a structured McpToolException."
```

---

## Task 8: Group C — cancellation pass-through audit (find_references family)

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindReferencesLogic.cs` (and Tool if needed)
- Modify: `src/RoslynCodeLens/Tools/FindCallersLogic.cs`
- Modify: `src/RoslynCodeLens/Tools/AnalyzeChangeImpactLogic.cs` (delegates to the above)

### Step 1: For each Logic file

1. Confirm `ExecuteAsync` (or convert sync `Execute` to async) accepts `CancellationToken ct = default` as its last parameter.
2. Pass `ct` to every awaited Roslyn API in the body (especially `SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken: ct)`).
3. At the top of any outer `foreach (var project in ...)` or `foreach (var syntaxTree in ...)` loop, insert `ct.ThrowIfCancellationRequested();`.
4. Update Tool wrapper to take `CancellationToken ct = default` and pass it through.

> **Important:** today, `FindReferencesLogic.Execute` is sync. Converting it to async is a breaking signature change. **Defer this:** if it's currently sync, leave it sync; cancellation only matters for the async tools. Update the plan to skip this Task if the Logic is sync. Only audit async logic.

### Step 2: Test cancellation

```csharp
[Fact]
public async Task FindReferences_PreCancelledToken_ThrowsOperationCancelled()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await FindReferencesLogic.ExecuteAsync(_loaded, _resolver, _metadata, "IGreeter", cts.Token));
}
```

Only add this test for tools whose Logic is genuinely async (use `Task<T>` return).

### Step 3-5: Standard pattern.

```
git commit -am "feat(errors): honor CancellationToken in find_references family"
```

---

## Task 9: Group C — cancellation pass-through audit (diagnostics + analyzers)

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetDiagnosticsLogic.cs` (already async)
- Modify: `src/RoslynCodeLens/AnalyzerRunner.cs` (or wherever `RunAnalyzersAsync` lives)

### Audit

`GetDiagnosticsLogic.ExecuteAsync` already takes `ct` and passes it to `AnalyzerRunner.RunAnalyzersAsync`. Verify:

1. The per-project loop in `ExecuteAsync` has `ct.ThrowIfCancellationRequested()` at the top.
2. `AnalyzerRunner.RunAnalyzersAsync` passes `ct` to the actual Roslyn `WithAnalyzers(...).GetAnalyzerDiagnosticsAsync(ct)` call.

### Test

```csharp
[Fact]
public async Task GetDiagnostics_PreCancelledToken_ThrowsOperationCancelled()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    using var tempFile = new TempTrustFile();
    var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
    trustStore.AddSessionTrust(_loaded.Solution.FilePath!);
    var allowlist = ...;

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: true,
            trustStore, allowlist, cts.Token));
}
```

### Commit

```
git commit -am "feat(errors): honor CancellationToken in get_diagnostics + analyzer runner"
```

---

## Task 10: Group C — cancellation audit (remaining high-latency async tools)

**Files (one per tool, atomic commits):**
- `find_tests_for_symbol` (`FindTestsForSymbolLogic` if async; if sync, skip)
- `peek_il` (decompilation can be slow)
- `rebuild_solution` (`MultiSolutionManager.ForceReloadAsync`)
- `get_code_actions` / `get_code_fixes` / `apply_code_action` — all currently async with `ct`; verify `ct` flows to `CodeFixProvider.RegisterCodeFixesAsync` etc.
- `find_naming_violations`, `find_async_violations`, `find_disposable_misuse`, `find_unused_symbols` — these are currently sync per the grep. **Leave them sync for this PR.** Document in the design doc's "out of scope" section that sync-tool cancellation is a separate concern.

### Per tool: standard pattern

Add a `PreCancelled_ThrowsOperationCancelled` test; thread `ct` through. Commit per tool.

```
git commit -am "feat(errors): honor CancellationToken in <tool_name>"
```

---

## Task 11: Docs catch-up

**Files:**
- Modify: `README.md` — add a short "Error responses" section after "Response shape"
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — add "Error codes" section near the top with the catalog
- Modify: `docs/site/docs/index.md` — note in the "Working with results" callout

### README addition

```markdown
## Error responses

When a tool can't proceed (symbol not resolved, solution not trusted, file not found, …),
the response is an `isError: true` content block carrying a structured JSON body:

```json
{
  "code": "SolutionNotTrusted",
  "message": "Solution 'Foo.sln' is not trusted for analyzer execution. …",
  "details": { "solutionPath": "C:\\Foo.sln" }
}
```

Codes: `SymbolNotFound`, `SolutionNotTrusted`, `AmbiguousMatch`, `FileNotFound`,
`ProjectNotFound`, `InvalidArgument`, `Internal`. Switch on `code` to handle each.
```

### SKILL.md addition

Mirror the README block in a "Error codes" section.

### Commit

```
git commit -am "docs: document structured error responses and ToolErrorCode catalog"
```

---

## Task 12: Final verification + PR

### Step 1: Build clean

```
dotnet build
```

Must succeed with 0 errors. Pre-existing warnings stay (211 analyzer warnings on dependency-vulnerable transitive packages — not ours).

### Step 2: Full test suite

```
dotnet test
```

Expected: all tests green except the known `IsAdapterRestoreFlake` family (CS0103/CS0234/CS0246 in NUnit/MSTest/XUnit fixtures — environmental). Document any new failures and triage before opening the PR.

### Step 3: Manual smoke test

Restart Claude / your MCP client. Call:

1. `set_active_solution("nonexistent")` → expect `isError: true` with `code: "ProjectNotFound"` in the response body.
2. `get_diagnostics({ includeAnalyzers: true })` on an untrusted solution → expect `code: "SolutionNotTrusted"`.
3. `analyze_method("Foo.Nonexistent")` → expect `code: "SymbolNotFound"`.

If any of those return the old free-form error message instead of a structured JSON body, Task 2's wiring is wrong — revisit.

### Step 4: Open PR

Push the branch, open a PR titled `feat: structured tool errors + cancellation pass-through`. PR body should include:

- The new `ToolErrorCode` catalog
- An example before/after error response showing the JSON shape
- A **BREAKING CHANGES** callout listing the five Group B tools whose nullable returns now throw
- A note about cancellation: "The MCP framework was already supplying CancellationToken; this PR makes ~11 long-running tools actually honor it."

---

## Gotchas

- **Don't add try/catch in `*Tool.cs` Execute methods.** The whole point is the single conversion point at the MCP host. If you find yourself adding per-tool try/catch, you've taken a wrong turn — revisit Task 2's wiring.
- **`OperationCanceledException` must NOT be caught/formatted** by the new filter. MCP has native cancellation semantics; converting OCE to a structured error muddies that. The formatter explicitly throws if asked to format an OCE — that's the guardrail.
- **Don't convert bespoke `Success: bool` result types** (`CodeActionResult`, `FindAsyncViolationsResult`, etc.). They encode tool-specific "ran but reports failure" — different from "cannot proceed".
- **Sync tools stay sync.** Adding CT param to sync tools is performative — there's nothing inside to check it. The list of high-latency sync tools is documented in the design's "out of scope" section.
- **Verify by `inspect_external_assembly` first** before guessing the MCP SDK's filter API. The plan's Task 2 is intentionally vague there — research, then implement.
