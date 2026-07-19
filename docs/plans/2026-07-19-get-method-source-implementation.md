# get_method_source Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A `get_method_source` MCP tool (batch-capable via array input) returning members' full declaration source by name, with per-item statuses.

**Architecture:** Standard Tool/Logic split. `GetMethodSourceLogic` resolves each requested name through the existing `SymbolResolver`, expands overload groups, and slices the original declaration syntax (`DeclaringSyntaxReferences` → `ToFullString()`) so XML docs, attributes, and formatting survive verbatim. Design doc: `docs/plans/2026-07-19-get-method-source-design.md` — read it first; it fixes the item model, statuses, and resolution rules.

**Tech Stack:** Roslyn (existing deps only), xUnit, `RenameTestWorkspace` for isolated resolution tests.

**Working directory:** the `.worktrees/get-method-source` worktree, branch `feature/get-method-source`. All commands from its root.

**Conventions:** errors via `McpToolException(ToolErrorCode.X, msg, details)`; list envelope via `ToolListResult.Create(items, limit, summary)` (summary = anonymous object, see `FindReferencesTool.BuildSummary`); tools are `[McpServerToolType]` static classes using `manager.EnsureLoaded()` + `manager.GetAnalysisContext()` (`context.Loaded`, `context.Resolver`); string statuses/kinds (JSON-friendly); commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; hooks must pass (never `--no-verify`). The `ToolDescriptionMdxSafetyTests` gate applies to the new tool's `[Description]` texts — keep code-ish tokens in backticks, no bare `<`/`{`.

---

### Task 1: Model

**Files:**
- Create: `src/RoslynCodeLens/Models/MemberSourceInfo.cs`

**Step 1: Write the record**

```csharp
namespace RoslynCodeLens.Models;

/// <summary>
/// One requested member's source (or why it couldn't be returned).
/// Status: ok | notFound | ambiguous | metadata | unsupportedKind.
/// Kind (ok items): method | constructor | property | indexer | field | event.
/// </summary>
public record MemberSourceInfo(
    string RequestedSymbol,
    string Status,
    string? Symbol,
    string? Kind,
    string? File,
    int? StartLine,
    int? EndLine,
    string? Source,
    string? Project,
    IReadOnlyList<string>? Candidates = null);
```

**Step 2:** `dotnet build src/RoslynCodeLens` → 0 errors. Commit: `feat: MemberSourceInfo model for get_method_source`.

---

### Task 2: GetMethodSourceLogic (TDD — the whole matrix)

**Files:**
- Create: `tests/RoslynCodeLens.Tests/GetMethodSourceLogicTests.cs`
- Create: `src/RoslynCodeLens/Tools/GetMethodSourceLogic.cs`

**Step 1: Failing tests.** Test source (one file unless a test needs two):

```csharp
private const string DemoSource = """
    using System;
    namespace Demo;

    public class Widget
    {
        static Widget() { }
        public Widget(string name) { }

        /// <summary>Adds one.</summary>
        [Obsolete("use ComputeV2")]
        public int Compute(int value) => value + 1;
        public int Compute(int a, int b)
        {
            return a + b;
        }

        public string Name { get; set; } = "w";
        public int _count = 42;
        public event EventHandler? Changed;
    }

    public partial class Split { public partial void Run(); }
    public partial class Split { public partial void Run() { } }
    """;
```

Facts (call `GetMethodSourceLogic.Execute(resolver, metadata, symbols)` via a `RenameTestWorkspace`-built pair plus `new MetadataSymbolResolver(loaded, resolver)`):

1. `XmlDocAndAttribute_IncludedInSource` — `["Widget.Compute"]` → first item's `Source` contains `/// <summary>Adds one.</summary>`, `[Obsolete("use ComputeV2")]`, and `=> value + 1;`.
2. `OverloadGroup_ExpandsToAdjacentItems` — `["Widget.Compute"]` → exactly 2 `ok` items, both `Kind == "method"`, second's `Source` contains `int a, int b`.
3. `Property_ReturnsWholeDeclaration` — `["Widget.Name"]` → `Kind == "property"`, `Source` contains `{ get; set; }`.
4. `Field_ReturnsDeclarationStatement` — `["Widget._count"]` → `Kind == "field"`, `Source` contains `public int _count = 42;`.
5. `Event_Returns` — `["Widget.Changed"]` → `Kind == "event"`, `Source` contains `event EventHandler? Changed;`.
6. `CtorRequestForm_ReturnsInstanceAndStatic` — `["Widget.Widget"]` → 2 items `Kind == "constructor"` (instance with `string name`, static `static Widget()`).
7. `PartialMethod_OneItemPerPart` — `["Split.Run"]` → 2 `ok` items with different `Source` (`;`-form and body-form).
8. `NotFound_PerItem` — `["Widget.Nope", "Widget.Name"]` → item0 `Status=="notFound"` (null Source), item1 `ok` — order preserved.
9. `Ambiguous_ListsCandidates` — two files each declaring `Dup.Go` in different namespaces; `["Dup.Go"]` → `Status=="ambiguous"`, `Candidates.Count == 2`.
10. `Metadata_Status` — `["System.String.Concat"]` → at least one item, all `Status=="metadata"`, `Source` null.
11. `TypeName_UnsupportedKind` — `["Widget"]` → `Status=="unsupportedKind"`.
12. `EmptyInput_Throws` — `[]` → `McpToolException` with `ToolErrorCode.InvalidArgument`.
13. `LineSpan_IsAccurate` — `Widget.Name` item's `StartLine`/`EndLine` bracket the property line in `DemoSource`; `File == "Demo.cs"`.

**Step 2:** run `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~GetMethodSource"` → compile FAIL (logic missing) — correct.

**Step 3: Implement** `GetMethodSourceLogic`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class GetMethodSourceLogic
{
    public static IReadOnlyList<MemberSourceInfo> Execute(
        SymbolResolver resolver, MetadataSymbolResolver metadata, IReadOnlyList<string> symbols)
    {
        if (symbols.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "symbols must contain at least one member name.", new { });
        }

        var items = new List<MemberSourceInfo>();
        foreach (var requested in symbols)
            items.AddRange(ResolveOne(resolver, metadata, requested));
        return items;
    }

    private static IEnumerable<MemberSourceInfo> ResolveOne(
        SymbolResolver resolver, MetadataSymbolResolver metadata, string requested)
    {
        // Constructor request form: "Ns.Type.Type" (member segment == type simple name).
        var lastDot = requested.LastIndexOf('.');
        if (lastDot > 0)
        {
            var typePart = requested[..lastDot];
            var memberPart = requested[(lastDot + 1)..];
            if (string.Equals(typePart.Split('.')[^1], memberPart, StringComparison.Ordinal))
            {
                var ctorItems = CtorItems(resolver, requested, typePart).ToList();
                if (ctorItems.Count > 0) return ctorItems;
            }
        }

        var matches = resolver.FindSymbols(requested);
        if (matches.Count == 0)
        {
            var resolved = metadata.Resolve(requested);
            if (resolved != null)
                return [NotInSource(requested, resolved.Symbol)];
            return [new MemberSourceInfo(requested, "notFound", null, null, null, null, null, null, null)];
        }

        // Overloads of one method are ONE logical request; distinct symbols are ambiguity.
        var groups = matches.GroupBy(GroupKey).ToList();
        if (groups.Count > 1)
        {
            return [new MemberSourceInfo(requested, "ambiguous", null, null, null, null, null, null, null,
                groups.Select(g => g.First().ToDisplayString()).ToList())];
        }

        return groups[0].SelectMany(s => Items(resolver, requested, s));
    }

    private static object GroupKey(ISymbol s) => s is IMethodSymbol m
        ? (m.ContainingType?.ToDisplayString() ?? "", m.Name)
        : s.ToDisplayString();

    private static IEnumerable<MemberSourceInfo> CtorItems(
        SymbolResolver resolver, string requested, string typeName)
    {
        foreach (var type in resolver.FindSymbols(typeName).OfType<INamedTypeSymbol>())
        {
            foreach (var ctor in type.InstanceConstructors.Concat(type.StaticConstructors))
            {
                foreach (var item in Items(resolver, requested, ctor))
                    yield return item;
            }
        }
    }

    private static IEnumerable<MemberSourceInfo> Items(
        SymbolResolver resolver, string requested, ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol)
        {
            yield return new MemberSourceInfo(requested, "unsupportedKind", symbol.ToDisplayString(),
                null, null, null, null,
                null, null);
            yield break;
        }

        var refs = symbol.DeclaringSyntaxReferences;
        if (refs.Length == 0 || !symbol.Locations.Any(l => l.IsInSource))
        {
            yield return NotInSource(requested, symbol);
            yield break;
        }

        foreach (var reference in refs)
        {
            var node = reference.GetSyntax();
            // Fields/events resolve to the variable declarator; the useful source is
            // the whole declaration statement (modifiers, type, initializer).
            if (node is VariableDeclaratorSyntax declarator)
                node = declarator.Parent?.Parent ?? node;

            // Implicit accessors etc. — climb to the member declaration.
            while (node is not MemberDeclarationSyntax && node.Parent != null)
                node = node.Parent;

            var span = node.GetLocation().GetLineSpan();
            yield return new MemberSourceInfo(
                requested, "ok", symbol.ToDisplayString(), KindOf(symbol),
                span.Path, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1,
                node.ToFullString().Trim('\r', '\n'),
                resolver.GetProjectName(symbol));
        }
    }

    private static MemberSourceInfo NotInSource(string requested, ISymbol symbol)
        => new(requested, "metadata", symbol.ToDisplayString(), KindOf(symbol),
            null, null, null, null, null);

    private static string KindOf(ISymbol s) => s switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "method",
    };
}
```

Reference code — verify against real APIs while implementing (notably: `MetadataSymbolResolver.Resolve` result shape; whether `Trim('\r','\n')` should instead trim leading blank lines while keeping indentation of the first content line — assert what tests demand). Note `unsupportedKind` for types must apply when `FindSymbols` returns a type; the message-free record is fine, the SKILL doc carries the guidance.

**Step 4:** filter run → all 13 PASS. Debug root causes; don't weaken.

**Step 5:** Commit: `feat: get_method_source resolution and source extraction`.

---

### Task 3: Tool wrapper + fixture test

**Files:**
- Create: `src/RoslynCodeLens/Tools/GetMethodSourceTool.cs`
- Create: `tests/RoslynCodeLens.Tests/GetMethodSourceFixtureTests.cs`

Wrapper (verify context member names against `FindReferencesTool`):

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetMethodSourceTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "get_method_source"),
     Description("Return the full declaration source (XML docs, attributes, signature, body — original " +
                 "formatting) of one or more members by name: methods (all overloads returned), " +
                 "constructors (request as `Type.TypeName`), properties, indexers, fields, events. " +
                 "Batch-friendly: pass many names in one call instead of reading whole files. " +
                 "Per-item statuses: ok, notFound, ambiguous (with candidates), metadata (use `peek_il` " +
                 "or `inspect_external_assembly`), unsupportedKind (whole types — use `get_type_overview`). " +
                 "Items keep request order.")]
    public static ToolListResult<MemberSourceInfo> Execute(
        MultiSolutionManager manager,
        [Description("Member names: simple (`MyClass.MyMethod`) or fully qualified (`Ns.MyClass.MyMethod`)")] string[] symbols,
        [Description("Maximum number of items to return (default: 100)")] int? limit = null)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        var items = GetMethodSourceLogic.Execute(context.Resolver, context.Metadata, symbols);

        var summary = new
        {
            byStatus = new
            {
                ok = items.Count(i => string.Equals(i.Status, "ok", StringComparison.Ordinal)),
                notFound = items.Count(i => string.Equals(i.Status, "notFound", StringComparison.Ordinal)),
                ambiguous = items.Count(i => string.Equals(i.Status, "ambiguous", StringComparison.Ordinal)),
                metadata = items.Count(i => string.Equals(i.Status, "metadata", StringComparison.Ordinal)),
                unsupportedKind = items.Count(i => string.Equals(i.Status, "unsupportedKind", StringComparison.Ordinal)),
            },
        };
        return ToolListResult.Create(items, limit ?? DefaultLimit, summary);
    }
}
```

Fixture test (`[Collection("TestSolution")]`): `["Greeter.Greet"]` → single `ok` item, `Source` contains the method's actual body text (Read `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/Greeter.cs` first and assert on a distinctive line), `Project == "TestLib"`, `File` ends with `Greeter.cs`.

Run: build clean; `--filter "FullyQualifiedName~GetMethodSource"` → 14 PASS; the MDX-safety test still green (`--filter "FullyQualifiedName~ToolDescriptionMdxSafety"`). Commit: `feat: expose get_method_source MCP tool`.

---

### Task 4: Docs + verification

- SKILL.md (worktree copy): Red Flags row `| "Let me \`Read\` the file to see this method's body" / "Show me the source of these 5 methods" | \`get_method_source\` |`; update the **Before `Read`ing a `.cs` file** checklist item 2/3 to mention `get_method_source` for bodies; bullet in "Understanding a Codebase" after `analyze_method`; Quick Reference row; metadata-support row (`No — source only; metadata members reported with status "metadata"`).
- CLAUDE.md: "59" → "60 code intelligence tools".
- tools/DocGen/Program.cs categoryMap: `["get_method_source"] = "analysis",`.
- docs/BACKLOG.md: §5 `get_method_source` bullet → `✅ *shipped* (PR #<n>)` style + Recently shipped row (`get_method_source | Analysis | #<n>` — fill the PR number at PR time or reference the branch and fix on merge; prefer opening the PR first then committing docs? No — cite "this PR" via the design doc link and update the number in the PR itself if known; acceptable to write the row with the PR number once `gh pr create` returns it, docs commit after PR creation is NOT possible — so: leave the Recently-shipped row's PR cell as `#304+1` placeholder? NO — simplest: commit docs BEFORE the PR with the row reading `| get_method_source | Analysis | (this PR) |` and let the next docs touch fix the number, OR create the PR first and push a follow-up docs commit with the real number. Choose: push branch, create PR, then add the docs commit with the real number and push again — CI re-runs, PR stays one unit.)
- Full `dotnet build` + `dotnet test` (expect ~749+ green), fixture-pristine check.

Commit(s): `docs: document get_method_source (60 tools)`.

---

## Deviations
Report any API mismatch or trivia-trimming decision in the final report; design-relevant ones append to the design doc.
