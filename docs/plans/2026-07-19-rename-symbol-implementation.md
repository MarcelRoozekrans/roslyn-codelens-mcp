# rename_symbol Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A `rename_symbol` MCP tool that safely renames a type or member across the whole solution via Roslyn's `Renamer` API, with preview-by-default and a diagnostics-delta conflict check.

**Architecture:** New `RenameSymbolLogic` (pure logic, testable) + thin `RenameSymbolTool` MCP wrapper, following the existing Tool/Logic split. Disk writing and `TextEdit` diffing are extracted from `CodeActionRunner` into a shared `SolutionChangeWriter` so both tools use one write path. Design doc: `docs/plans/2026-07-19-rename-symbol-design.md` (read it first).

**Tech Stack:** .NET / C#, Roslyn (`Microsoft.CodeAnalysis.Rename.Renamer`, already available via the existing Workspaces dependency), xUnit with an `AdhocWorkspace`-based test helper (isolated from the shared `TestSolutionFixture` — no fixture source files are modified).

**Working directory:** the `.worktrees/rename-symbol` worktree, branch `feature/rename-symbol`. All paths below are relative to that worktree root. Run all `dotnet` commands from the worktree root.

**Conventions you must follow** (from the existing codebase):
- Errors are thrown as `McpToolException(ToolErrorCode.X, message, detailsObject)` — see `src/RoslynCodeLens/Tools/AnalyzeMethodLogic.cs:14` for the pattern. The MCP layer converts them to the `{code, message, details}` envelope; logic code just throws.
- Single-object results are plain records in `src/RoslynCodeLens/Models/` (no list envelope) — like `CodeActionResult`.
- Tools are `[McpServerToolType]` static classes with `[McpServerTool(Name = "...")]` — copy the shape of `src/RoslynCodeLens/Tools/ApplyCodeActionTool.cs`.
- Tests: xUnit, `Assert.*`, file-per-subject naming `<Subject>Tests.cs`.

---

### Task 1: Extract `SolutionChangeWriter` from `CodeActionRunner`

Pure refactor. The existing `apply_code_action` tests are the regression net — no new tests.

**Files:**
- Create: `src/RoslynCodeLens/SolutionChangeWriter.cs`
- Modify: `src/RoslynCodeLens/CodeActionRunner.cs` (remove `WriteChangesToDiskAsync` at ~line 155 and `ExtractTextEdits` at ~line 235, call the new helper instead)

**Step 1: Create the shared helper**

Move the two methods verbatim (public, renamed `ExtractTextEdits` → `ExtractTextEditsAsync`):

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

/// <summary>
/// Shared write path for tools that produce a changed Solution (apply_code_action,
/// rename_symbol): diff extraction for previews and document writes for apply mode.
/// </summary>
public static class SolutionChangeWriter
{
    public static async Task<List<TextEdit>> ExtractTextEditsAsync(
        Solution changedSolution, Solution originalSolution, CancellationToken ct)
    {
        // body of CodeActionRunner.ExtractTextEdits, unchanged
    }

    public static async Task WriteChangesToDiskAsync(
        Solution changedSolution, Solution originalSolution, CancellationToken ct)
    {
        // body of CodeActionRunner.WriteChangesToDiskAsync, unchanged
    }
}
```

In `CodeActionRunner.ApplyActionAsync`, replace the two call sites:

```csharp
var edits = await SolutionChangeWriter.ExtractTextEditsAsync(applyOp.ChangedSolution, project.Solution, ct).ConfigureAwait(false);
// ...
await SolutionChangeWriter.WriteChangesToDiskAsync(applyOp.ChangedSolution, project.Solution, ct).ConfigureAwait(false);
```

**Step 2: Build and run the regression net**

Run: `dotnet build src/RoslynCodeLens` → 0 errors.
Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ApplyCodeAction|FullyQualifiedName~CodeActionRunner"`
Expected: PASS (all existing apply/code-action tests).

**Step 3: Commit**

```bash
git add src/RoslynCodeLens/SolutionChangeWriter.cs src/RoslynCodeLens/CodeActionRunner.cs
git commit -m "refactor: extract SolutionChangeWriter from CodeActionRunner"
```

---

### Task 2: Result models

**Files:**
- Create: `src/RoslynCodeLens/Models/RenameConflict.cs`
- Create: `src/RoslynCodeLens/Models/RenameSymbolResult.cs`

**Step 1: Write the records**

```csharp
namespace RoslynCodeLens.Models;

/// <summary>A compiler error that would be introduced by applying the rename.</summary>
public record RenameConflict(string Id, string Message, string File, int Line);
```

```csharp
namespace RoslynCodeLens.Models;

public record RenameSymbolResult(
    bool Success,
    string OldName,
    string NewName,
    bool Applied,
    IReadOnlyList<TextEdit> Edits,
    int FilesChanged,
    IReadOnlyList<RenameConflict> Conflicts,
    string Message);
```

**Step 2: Build, commit**

Run: `dotnet build src/RoslynCodeLens` → 0 errors.

```bash
git add src/RoslynCodeLens/Models/RenameConflict.cs src/RoslynCodeLens/Models/RenameSymbolResult.cs
git commit -m "feat: add rename_symbol result models"
```

---

### Task 3: `RenameTestWorkspace` test helper

An `AdhocWorkspace`-based mini-solution builder so rename tests are isolated, fast, and never mutate the shared `TestSolutionFixture`. Precedent for `LoadedSolution` over `AdhocWorkspace`: `tests/RoslynCodeLens.Tests/LoaderConcurrencyHardeningTests.cs:40`.

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/RenameTestWorkspace.cs`

**Step 1: Write the helper**

```csharp
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynCodeLens.Tests.Fixtures;

/// <summary>
/// Builds an in-memory single-project LoadedSolution + SymbolResolver from source
/// strings. Pass absolute paths as file names when a test needs apply-mode disk writes.
/// </summary>
internal static class RenameTestWorkspace
{
    public static (LoadedSolution Loaded, SymbolResolver Resolver) Create(
        params (string FilePath, string Source)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Create(), "RenameProj", "RenameProj", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);
        foreach (var (path, source) in files)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), Path.GetFileName(path),
                SourceText.From(source), filePath: path);
        }

        var compilation = solution.GetProject(projectId)!
            .GetCompilationAsync().GetAwaiter().GetResult()!;

        var loaded = new LoadedSolution
        {
            Solution = solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation>(
                [new KeyValuePair<ProjectId, Compilation>(projectId, compilation)]),
        };
        return (loaded, new SymbolResolver(loaded));
    }
}
```

**Step 2: Smoke-check it compiles and resolves**

Add a temporary fact (deleted in Task 4 when real tests exist) or verify inline with the first Task 4 test — either way, by the end of Task 4 Step 2 the helper must be exercised. Run: `dotnet build tests/RoslynCodeLens.Tests` → 0 errors.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/RenameTestWorkspace.cs
git commit -m "test: add AdhocWorkspace-based helper for rename tests"
```

---

### Task 4: Validation and resolution errors (TDD)

**Files:**
- Create: `tests/RoslynCodeLens.Tests/RenameSymbolLogicTests.cs`
- Create: `src/RoslynCodeLens/Tools/RenameSymbolLogic.cs`

**Step 1: Write the failing tests**

Shared source used by most tests in this file — put it at the top of the test class:

```csharp
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class RenameSymbolLogicTests
{
    private const string BasicSource = """
        namespace RenameDemo;

        public class Widget
        {
            public Widget() { }
            public int Compute(int value) => value + 1;
            public int Compute(int a, int b) => a + b;
            // Widget appears in this comment.
            public string Marker = "Widget in a string";
            public string Describe() => nameof(Widget);
        }

        public class Gadget
        {
            public int Run() => new Widget().Compute(1);
        }
        """;

    private static Task<RenameSymbolResult> RunAsync(
        LoadedSolution loaded, SymbolResolver resolver, string symbol, string newName,
        bool renameOverloads = true, bool renameInStrings = false, bool renameInComments = true,
        bool preview = true, bool force = false)
        => RenameSymbolLogic.ExecuteAsync(
            loaded, resolver, symbol, newName,
            renameOverloads, renameInStrings, renameInComments, preview, force,
            CancellationToken.None);

    [Fact]
    public async Task InvalidIdentifier_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Widget", "123 bad name"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task UnknownSymbol_ThrowsSymbolNotFound()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "NoSuchType", "Whatever"));
        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }

    [Fact]
    public async Task AmbiguousSimpleName_ThrowsAmbiguousMatch()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("A.cs", "namespace NsA; public class Dup { }"),
            ("B.cs", "namespace NsB; public class Dup { }"));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Dup", "Renamed"));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
    }

    [Fact]
    public void ConstructorTarget_ThrowsInvalidArgument()
    {
        var (loaded, _) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var compilation = loaded.Compilations.Values.First();
        var widget = compilation.GetTypeByMetadataName("RenameDemo.Widget")!;
        var ctor = widget.InstanceConstructors.First(c => !c.IsImplicitlyDeclared);

        var ex = Assert.Throws<McpToolException>(
            () => RenameSymbolLogic.ValidateRenameTarget(ctor, "Widget.Widget"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void MetadataTarget_ThrowsInvalidArgument()
    {
        var (loaded, _) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var compilation = loaded.Compilations.Values.First();
        var stringType = compilation.GetSpecialType(SpecialType.System_String);

        var ex = Assert.Throws<McpToolException>(
            () => RenameSymbolLogic.ValidateRenameTarget(stringType, "System.String"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task MethodOverloadGroup_IsNotAmbiguous()
    {
        // Widget.Compute has two overloads; that is ONE rename target, not an ambiguity.
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget.Compute", "Calculate");
        Assert.True(result.Success);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~RenameSymbolLogic"`
Expected: FAIL — `RenameSymbolLogic` does not exist (compile error). That is the correct failure for this step.

**Step 3: Write the implementation**

`src/RoslynCodeLens/Tools/RenameSymbolLogic.cs` — complete file:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

public static class RenameSymbolLogic
{
    public static async Task<RenameSymbolResult> ExecuteAsync(
        LoadedSolution loaded, SymbolResolver resolver,
        string symbol, string newName,
        bool renameOverloads, bool renameInStrings, bool renameInComments,
        bool preview, bool force, CancellationToken ct)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{newName}' is not a valid C# identifier.", new { newName });
        }

        var target = ResolveSingleTarget(resolver, symbol);
        ValidateRenameTarget(target, symbol);

        var options = new SymbolRenameOptions(
            RenameOverloads: renameOverloads,
            RenameInStrings: renameInStrings,
            RenameInComments: renameInComments,
            RenameFile: false);

        var renamed = await Renamer.RenameSymbolAsync(
            loaded.Solution, target, options, newName, ct).ConfigureAwait(false);

        var edits = await SolutionChangeWriter.ExtractTextEditsAsync(
            renamed, loaded.Solution, ct).ConfigureAwait(false);
        var conflicts = await ComputeConflictsAsync(loaded.Solution, renamed, ct).ConfigureAwait(false);
        var filesChanged = edits.Select(e => e.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var oldName = target.ToDisplayString();

        if (preview)
        {
            return new RenameSymbolResult(true, oldName, newName, Applied: false,
                edits, filesChanged, conflicts,
                conflicts.Count > 0
                    ? $"{conflicts.Count} conflict(s) detected — applying would introduce new compiler errors."
                    : "Preview only — no files written. Re-run with preview=false to apply.");
        }

        if (conflicts.Count > 0 && !force)
        {
            return new RenameSymbolResult(false, oldName, newName, Applied: false,
                edits, filesChanged, conflicts,
                $"Refused to apply: {conflicts.Count} new compiler error(s) would be introduced. " +
                "Inspect Conflicts, or re-run with force=true to apply anyway.");
        }

        await SolutionChangeWriter.WriteChangesToDiskAsync(
            renamed, loaded.Solution, ct).ConfigureAwait(false);
        return new RenameSymbolResult(true, oldName, newName, Applied: true,
            edits, filesChanged, conflicts,
            $"Renamed {oldName} to {newName} in {filesChanged} file(s).");
    }

    internal static ISymbol ResolveSingleTarget(SymbolResolver resolver, string symbol)
    {
        var matches = resolver.FindSymbols(symbol);
        if (matches.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.SymbolNotFound,
                $"Symbol '{symbol}' not found.", new { symbol });
        }

        // Overloads of one method are a single rename target (Renamer handles the
        // group via RenameOverloads); everything else groups by full display string.
        var groups = matches.GroupBy(GroupKey).ToList();
        if (groups.Count > 1)
        {
            throw new McpToolException(ToolErrorCode.AmbiguousMatch,
                $"Symbol '{symbol}' matched {groups.Count} distinct symbols. Use a more qualified name.",
                new { matches = groups.Select(g => g.First().ToDisplayString()).ToList() });
        }

        return groups[0].First();
    }

    private static object GroupKey(ISymbol s) => s is IMethodSymbol m
        ? (m.ContainingType?.ToDisplayString() ?? "", m.Name)
        : s.ToDisplayString();

    internal static void ValidateRenameTarget(ISymbol target, string symbol)
    {
        if (target is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{symbol}' is a constructor — rename the containing type instead; constructors follow automatically.",
                new { symbol });
        }

        if (!target.Locations.Any(l => l.IsInSource))
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"'{symbol}' is a metadata symbol — only symbols defined in source can be renamed.",
                new { symbol });
        }
    }

    private static async Task<IReadOnlyList<RenameConflict>> ComputeConflictsAsync(
        Solution original, Solution renamed, CancellationToken ct)
    {
        var conflicts = new List<RenameConflict>();
        foreach (var change in renamed.GetChanges(original).GetProjectChanges())
        {
            var before = await change.OldProject.GetCompilationAsync(ct).ConfigureAwait(false);
            var after = await change.NewProject.GetCompilationAsync(ct).ConfigureAwait(false);
            if (before == null || after == null)
                continue;

            var beforeKeys = before.GetDiagnostics(ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(DiagnosticKey)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var diag in after.GetDiagnostics(ct)
                         .Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                if (beforeKeys.Contains(DiagnosticKey(diag)))
                    continue;

                var span = diag.Location.GetLineSpan();
                conflicts.Add(new RenameConflict(
                    diag.Id, diag.GetMessage(), span.Path,
                    span.StartLinePosition.Line + 1));
            }
        }
        return conflicts;
    }

    private static string DiagnosticKey(Diagnostic d)
        => $"{d.Id}|{d.Location.GetLineSpan().Path}|{d.GetMessage()}";
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~RenameSymbolLogic"`
Expected: PASS (all 6). If `MethodOverloadGroup_IsNotAmbiguous` fails inside the Renamer call rather than at resolution, debug there — the resolution grouping is what this task owns; use superpowers:systematic-debugging before changing anything else.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/RenameSymbolLogic.cs tests/RoslynCodeLens.Tests/RenameSymbolLogicTests.cs
git commit -m "feat: rename_symbol resolution, validation, and core rename via Roslyn Renamer"
```

---

### Task 5: Rename semantics — cascade and options (TDD)

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/RenameSymbolLogicTests.cs`

All tests use `BasicSource` and preview mode; assert on the returned `Edits`. Helper for assertions — add to the test class:

```csharp
private static string ApplyEditsToSource(string source, IEnumerable<TextEdit> edits, string filePath)
{
    var text = Microsoft.CodeAnalysis.Text.SourceText.From(source);
    var changes = edits
        .Where(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        .Select(e => new Microsoft.CodeAnalysis.Text.TextChange(
            Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                text.Lines[e.StartLine - 1].Start + e.StartColumn - 1,
                text.Lines[e.EndLine - 1].Start + e.EndColumn - 1),
            e.NewText));
    return text.WithChanges(changes).ToString();
}
```

**Step 1: Write the failing tests**

```csharp
[Fact]
public async Task RenameType_CascadesToUsagesCtorAndNameof()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
    var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");

    Assert.True(result.Success);
    Assert.False(result.Applied);
    Assert.Empty(result.Conflicts);

    var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
    Assert.Contains("public class Sprocket", after, StringComparison.Ordinal);
    Assert.Contains("public Sprocket()", after, StringComparison.Ordinal);          // ctor follows
    Assert.Contains("new Sprocket().Compute(1)", after, StringComparison.Ordinal);  // usage follows
    Assert.Contains("nameof(Sprocket)", after, StringComparison.Ordinal);           // nameof follows
    Assert.DoesNotContain("class Widget", after, StringComparison.Ordinal);
}

[Fact]
public async Task RenameInComments_OnByDefault_RewritesComment()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
    var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");
    var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
    Assert.Contains("// Sprocket appears in this comment.", after, StringComparison.Ordinal);
}

[Fact]
public async Task RenameInComments_Off_LeavesComment()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
    var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", renameInComments: false);
    var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
    Assert.Contains("// Widget appears in this comment.", after, StringComparison.Ordinal);
}

[Fact]
public async Task RenameInStrings_OffByDefault_LeavesString()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
    var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");
    var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
    Assert.Contains("\"Widget in a string\"", after, StringComparison.Ordinal);
}

[Fact]
public async Task RenameInStrings_On_RewritesString()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
    var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", renameInStrings: true);
    var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
    Assert.Contains("\"Sprocket in a string\"", after, StringComparison.Ordinal);
}

[Fact]
public async Task RenameOverloads_On_RenamesAllOverloadsAndCallSites()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
    var result = await RunAsync(loaded, resolver, "Widget.Compute", "Calculate");
    var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
    Assert.Contains("public int Calculate(int value)", after, StringComparison.Ordinal);
    Assert.Contains("public int Calculate(int a, int b)", after, StringComparison.Ordinal);
    Assert.Contains(".Calculate(1)", after, StringComparison.Ordinal);
    Assert.Empty(result.Conflicts);
}
```

**Step 2: Run tests to verify they fail or pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~RenameSymbolLogic"`
Expected: PASS — Task 4's implementation already covers these; this task pins the semantics. If any FAIL, the Renamer options are wired wrong (e.g. positional order of `SymbolRenameOptions`): fix `RenameSymbolLogic`, do not weaken the test.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/RenameSymbolLogicTests.cs
git commit -m "test: pin rename_symbol cascade and option semantics"
```

---

### Task 6: Conflict detection and force (TDD)

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/RenameSymbolLogicTests.cs`

**Step 1: Write the failing tests**

```csharp
private const string CollisionSource = """
    namespace RenameDemo;
    public class First { }
    public class Second { }
    """;

[Fact]
public async Task CollidingRename_Preview_ReportsConflicts()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Types.cs", CollisionSource));
    var result = await RunAsync(loaded, resolver, "First", "Second");

    Assert.True(result.Success);
    Assert.False(result.Applied);
    Assert.NotEmpty(result.Conflicts);   // CS0101: duplicate type in namespace
    Assert.Contains(result.Conflicts, c => string.Equals(c.Id, "CS0101", StringComparison.Ordinal));
}

[Fact]
public async Task CollidingRename_Apply_RefusesWithoutForce()
{
    var (loaded, resolver) = RenameTestWorkspace.Create(("Types.cs", CollisionSource));
    var result = await RunAsync(loaded, resolver, "First", "Second", preview: false);

    Assert.False(result.Success);
    Assert.False(result.Applied);
    Assert.NotEmpty(result.Conflicts);
    Assert.Contains("force", result.Message, StringComparison.OrdinalIgnoreCase);
}
```

Note: neither test writes to disk (in-memory documents with relative paths; the refusal path returns before writing). The `force=true` disk-write case is covered in Task 7 where real temp files exist.

**Step 2: Run tests**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~RenameSymbolLogic"`
Expected: PASS (conflict logic shipped in Task 4; these pin it). If `CS0101` is not reported, debug `ComputeConflictsAsync` — likely the before/after key comparison.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/RenameSymbolLogicTests.cs
git commit -m "test: pin rename_symbol conflict detection and force refusal"
```

---### Task 7: Apply mode writes to disk (TDD)

**Files:**
- Modify: `tests/RoslynCodeLens.Tests/RenameSymbolLogicTests.cs`

**Step 1: Write the failing tests**

Real temp files so `WriteChangesToDiskAsync` has somewhere to write. Always clean up.

```csharp
[Fact]
public async Task Apply_WritesRenamedFilesToDisk()
{
    var dir = Directory.CreateTempSubdirectory("rename-apply-").FullName;
    try
    {
        var path = Path.Combine(dir, "Widget.cs");
        await File.WriteAllTextAsync(path, BasicSource);
        var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false);

        Assert.True(result.Success);
        Assert.True(result.Applied);
        var onDisk = await File.ReadAllTextAsync(path);
        Assert.Contains("public class Sprocket", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain("class Widget", onDisk, StringComparison.Ordinal);
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
}

[Fact]
public async Task Preview_LeavesDiskUntouched()
{
    var dir = Directory.CreateTempSubdirectory("rename-preview-").FullName;
    try
    {
        var path = Path.Combine(dir, "Widget.cs");
        await File.WriteAllTextAsync(path, BasicSource);
        var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");   // preview: true

        Assert.False(result.Applied);
        Assert.Equal(BasicSource, await File.ReadAllTextAsync(path));
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
}

[Fact]
public async Task CollidingRename_ForceTrue_WritesAnyway()
{
    var dir = Directory.CreateTempSubdirectory("rename-force-").FullName;
    try
    {
        var path = Path.Combine(dir, "Types.cs");
        await File.WriteAllTextAsync(path, CollisionSource);
        var (loaded, resolver) = RenameTestWorkspace.Create((path, CollisionSource));

        var result = await RunAsync(loaded, resolver, "First", "Second", preview: false, force: true);

        Assert.True(result.Applied);
        Assert.NotEmpty(result.Conflicts);
        Assert.Contains("class Second", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }
}
```

**Step 2: Run tests**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~RenameSymbolLogic"`
Expected: PASS. The write path is `SolutionChangeWriter` (Task 1), already proven by the apply_code_action suite; failures here point at the preview/force gating in `ExecuteAsync`.

**Step 3: Commit**

```bash
git add tests/RoslynCodeLens.Tests/RenameSymbolLogicTests.cs
git commit -m "test: pin rename_symbol apply-mode disk writes, preview isolation, and force"
```

---

### Task 8: MCP tool wrapper + fixture integration test

**Files:**
- Create: `src/RoslynCodeLens/Tools/RenameSymbolTool.cs`
- Create: `tests/RoslynCodeLens.Tests/RenameSymbolFixtureTests.cs`

**Step 1: Write the tool wrapper**

Copy the shape of `ApplyCodeActionTool` / `FindReferencesTool` (manager + `EnsureLoaded` + `GetAnalysisContext`):

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class RenameSymbolTool
{
    [McpServerTool(Name = "rename_symbol"),
     Description("Safely rename a type or member across the entire solution (Roslyn Renamer). " +
                 "Cascades to references, constructors, overrides, nameof, and XML doc crefs. " +
                 "Defaults to preview mode (returns edits without writing files); set preview=false to apply. " +
                 "New compiler errors the rename would introduce are reported as Conflicts, and apply mode " +
                 "refuses to write them unless force=true. Locals/parameters and file renames are not supported.")]
    public static async Task<RenameSymbolResult> Execute(
        MultiSolutionManager manager,
        [Description("Symbol to rename: simple type (MyClass), fully qualified (Namespace.MyClass), or member (MyClass.MyMethod)")] string symbol,
        [Description("New name — a bare C# identifier, e.g. 'OrderProcessor'")] string newName,
        [Description("Rename all overloads of a method together (default: true; false renames a single arbitrary overload)")] bool renameOverloads = true,
        [Description("Also rewrite occurrences inside string literals (default: false)")] bool renameInStrings = false,
        [Description("Also rewrite occurrences inside comments (default: true)")] bool renameInComments = true,
        [Description("Preview only — return edits without writing to disk (default: true)")] bool preview = true,
        [Description("Apply even when Conflicts are reported (default: false)")] bool force = false,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return await RenameSymbolLogic.ExecuteAsync(
            context.Loaded, context.Resolver, symbol, newName,
            renameOverloads, renameInStrings, renameInComments, preview, force, ct).ConfigureAwait(false);
    }
}
```

Check `manager.GetAnalysisContext()`'s actual member names against another tool (`src/RoslynCodeLens/Tools/FindReferencesTool.cs:22-23`) before assuming `context.Loaded` / `context.Resolver`.

**Step 2: Write the integration test (failing first if wrapper doesn't compile)**

One preview-only test against the real MSBuildWorkspace-loaded fixture, proving name-based end-to-end rename over a multi-project solution. Preview mode guarantees the shared fixture's files are never modified.

```csharp
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

[Collection("TestSolution")]
public class RenameSymbolFixtureTests
{
    private readonly TestSolutionFixture _fixture;

    public RenameSymbolFixtureTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PreviewRenameGreeter_ProducesEditsAcrossProjects()
    {
        var result = await RenameSymbolLogic.ExecuteAsync(
            _fixture.Loaded, _fixture.Resolver, "Greeter", "Salutations",
            renameOverloads: true, renameInStrings: false, renameInComments: true,
            preview: true, force: false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Applied);
        Assert.Empty(result.Conflicts);
        // Greeter is defined in TestLib and called from the xUnit/NUnit/MSTest fixture
        // projects (see TestSolutionFixture health probe), so edits must span >1 project.
        var projects = result.Edits
            .Select(e => Path.GetFileName(Path.GetDirectoryName(e.FilePath)!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.True(projects.Count > 1,
            $"Expected edits across multiple projects, got: {string.Join(", ", projects)}");
    }
}
```

**Step 3: Run**

Run: `dotnet build src/RoslynCodeLens` → 0 errors.
Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~RenameSymbolFixture"`
Expected: PASS. (Fixture load takes a while on cold start — that is the shared fixture, not a hang.)

**Step 4: Verify the shared fixture was not modified**

Run: `git status --short tests/RoslynCodeLens.Tests/Fixtures/`
Expected: no output. If fixture files changed, a test wrote to disk that must not — fix before committing.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/RenameSymbolTool.cs tests/RoslynCodeLens.Tests/RenameSymbolFixtureTests.cs
git commit -m "feat: expose rename_symbol MCP tool"
```

---

### Task 9: Documentation

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `docs/BACKLOG.md`

**Step 1: SKILL.md updates** (keep each addition in the file's existing voice; find sections by heading):

1. **Red Flags table** (after the `apply_code_action`-adjacent rows): add
   `| "Rename this class/method everywhere" / "Let me edit N files to change this name" | \`rename_symbol\` |`
2. **Diagnostics section**, after the `apply_code_action` bullet list: add
   `- \`rename_symbol\` — solution-wide safe rename of a type or member (Roslyn Renamer; NOT available via apply_code_action). Preview by default; new-compiler-error conflicts reported; apply refuses on conflicts unless force=true.`
3. **Tool Quick Reference table**: add
   `| \`rename_symbol\` | "Rename this symbol everywhere" / "Change this name across the solution" |`
4. **Metadata Support by Tool table**: add
   `| \`rename_symbol\` | No — source only | Locals/parameters and file renames unsupported | |`

**Step 2: BACKLOG.md update**

In §5 High value: mark the `rename_symbol` bullet as in progress, e.g. prefix with `🔧 **In flight:**` and reference the design doc `docs/plans/2026-07-19-rename-symbol-design.md`. Add a line under `## In flight`: `- **\`rename_symbol\`** — branch \`feature/rename-symbol\`, design docs/plans/2026-07-19-rename-symbol-design.md`.

**Step 3: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md docs/BACKLOG.md
git commit -m "docs: document rename_symbol in skill and backlog"
```

---

### Task 10: Full verification

**Step 1: Full build + full test suite**

Run: `dotnet build` → 0 errors, 0 warnings introduced by this branch.
Run: `dotnet test` → all tests pass (known fixture flake auto-retries per `TestSolutionFixture`; a hard failure after 6 attempts is real).

**Step 2: Clean tree check**

Run: `git status --short` → only expected untracked/modified files (none, after commits).

**Step 3: Wrap up**

Use superpowers:verification-before-completion, then superpowers:finishing-a-development-branch (options: PR to `main` from `feature/rename-symbol`).

---

## Deviations

Any deviation from this plan (API mismatch, failing assumption about Renamer behavior on AdhocWorkspace, `GetAnalysisContext` member names) gets noted in the final report and, if design-relevant, appended to the design doc's Out-of-scope/notes section.
