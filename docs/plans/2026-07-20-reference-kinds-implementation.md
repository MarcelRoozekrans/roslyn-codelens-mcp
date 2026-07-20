# find_references Reference-Kind Classification — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enhance `find_references` to tag each reference with a precise kind (read/write/readwrite/invocation/method_group/object_creation/cast/type_check/typeof/base_type/type_constraint/type_argument/declaration/attribute/nameof/xml_doc), report per-occurrence with a `Column`, and accept a server-side `kinds` filter.

**Architecture:** A new pure `ReferenceClassifier` (no I/O) maps `(referenceNode, referencedSymbol, semanticModel)` → kind string via a syntax-parent walk (Roslyn `IsWrittenTo` pattern), using the document's semantic model only for implicit `ref`/`out` parameter resolution. `FindReferencesLogic` calls it per occurrence, dedupes by `(file, line, column)`, and populates the enlarged `SymbolReference`. The tool wrapper filters by `kinds` and adds a `byKind` summary. Design doc: `docs/plans/2026-07-20-reference-kinds-design.md` — read it first; it fixes the taxonomy and algorithm.

**Tech Stack:** Roslyn (existing deps), xUnit, `RenameTestWorkspace` for isolated tests.

**Working directory:** the `.worktrees/reference-kinds` worktree, branch `feature/reference-kinds`. All commands from its root.

**Conventions:** errors via `McpToolException(ToolErrorCode.X, msg, details)`; list envelope via `ToolListResult.Create(items, limit, summary)`; string kinds (not enums); commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; hooks must pass (never `--no-verify`). Tool `[Description]` must satisfy `ToolDescriptionMdxSafetyTests` (backtick code tokens, no bare `<`/`{`).

**Context facts (verified):** the ONLY `new SymbolReference(...)` site is `FindReferencesLogic.cs:62`. `analyze_change_impact` (`AnalyzeChangeImpactLogic.cs:10`) reuses `FindReferencesLogic.Execute` output and never inspects `ReferenceKind`. Existing `FindReferencesToolTests.cs` does NOT assert kind strings, so no test migration needed there — but grep for any kind-string assertions before finishing (`assignment`/`argument`/`instantiation`/`base_type`/`usage`) and update any that surface.

---

### Task 1: Enlarge the model

**Files:**
- Modify: `src/RoslynCodeLens/Models/SymbolReference.cs`
- Modify: `src/RoslynCodeLens/Tools/FindReferencesLogic.cs:62` (add a placeholder column arg to keep it compiling)

**Step 1:** Change the record to add `Column` after `Line`:

```csharp
namespace RoslynCodeLens.Models;

public record SymbolReference(
    string ReferenceKind,
    string File,
    int Line,
    int Column,
    string Snippet,
    string Project,
    bool IsGenerated = false);
```

**Step 2:** In `FindReferencesLogic.ScanForReferences`, pass a column so it builds. Minimal change now (Task 3 does the real work):

```csharp
var column = lineSpan.StartLinePosition.Character + 1;
// ...
results.Add(new SymbolReference(
    kind, file, line, column, snippet, projectName, resolver.IsGenerated(file)));
```

**Step 3:** `dotnet build src/RoslynCodeLens` → 0 errors. `dotnet build tests/RoslynCodeLens.Tests` → 0 errors (fix any test that positionally constructed `SymbolReference` — there should be none; grep `new SymbolReference` in tests).

**Step 4:** Commit: `feat: add Column to SymbolReference for per-occurrence references`.

---

### Task 2: ReferenceClassifier (TDD — the taxonomy matrix)

**Files:**
- Create: `tests/RoslynCodeLens.Tests/ReferenceClassifierTests.cs`
- Create: `src/RoslynCodeLens/Analysis/ReferenceClassifier.cs`

**Step 1: Write the failing tests.** Fixture source + a helper that classifies every occurrence of a named identifier. Put this scaffolding at the top of the test class:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests;

public class ReferenceClassifierTests
{
    // Classifies every IdentifierName/GenericName occurrence whose bound symbol's name == targetName.
    // Returns (lineText-ish token context, kind) for assertion. Uses one compilation.
    private static List<string> KindsOf(string source, string targetName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var comp = CSharpCompilation.Create("C", [tree], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = comp.GetSemanticModel(tree);
        var kinds = new List<string>();
        foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (name.Identifier.Text != targetName) continue;
            var symbol = model.GetSymbolInfo(name).Symbol ?? model.GetDeclaredSymbol(name);
            if (symbol == null) continue;
            // Skip the declaration site itself when it is a pure declarator (we classify references).
            kinds.Add(ReferenceClassifier.Classify(name, symbol, model));
        }
        return kinds;
    }

    private static string OneKind(string source, string target)
        => Assert.Single(KindsOf(source, target).Where(k => k != "declaration"));
}
```

Then the matrix (each a `[Fact]`; keep sources tiny and self-contained). Adjust the `KindsOf`/`OneKind` filtering as needed so each test isolates the occurrence it means to check — a `[Theory]` is fine if cleaner:

1. **Field read** — `class C { int _x; int R() => _x; }`, target `_x` → contains `read`.
2. **Field write** — `class C { int _x; void W() { _x = 1; } }` → `_x =` occurrence is `write`.
3. **Field readwrite compound** — `_x += 1;` → `readwrite`.
4. **Field increment** — `_x++;` and `--_x;` → `readwrite`.
5. **out argument** — `class C { void M(out int x){x=0;} void U(){int v; M(out v);} }` target `v` → the `out v` occurrence is `write`.
6. **ref argument** — `void M(ref int x){} ... int v=0; M(ref v);` → `ref v` is `readwrite`.
7. **read argument (by value)** — `Console-like Use(v)` plain arg → `read`.
8. **method invocation** — `class C { void M(){} void U(){ M(); } }` target `M` → `invocation`.
9. **method group** — `class C { void H(){} System.Action U()=> H; }` (or `Select(H)`) target `H` → `method_group`.
10. **object_creation** — `class Foo{} class C{ Foo U()=> new Foo(); }` target `Foo` → the `new Foo()` occurrence is `object_creation`.
11. **cast (explicit)** — `object o=null; var f=(Foo)o;` target `Foo` → `cast`.
12. **cast (as)** — `var f = o as Foo;` → `cast`.
13. **type_check is** — `if (o is Foo) {}` → `type_check`.
14. **type_check declaration pattern** — `if (o is Foo f) {}` → `type_check`.
15. **type_check switch** — `switch(o){ case Foo f: break; }` → `type_check`.
16. **typeof** — `var t = typeof(Foo);` → `typeof`.
17. **base_type** — `class Bar : Foo {}` target `Foo` → `base_type`.
18. **type_constraint** — `class C<T> where T : Foo {}` target `Foo` → `type_constraint`.
19. **type_argument** — `var l = new System.Collections.Generic.List<Foo>();` target `Foo` → `type_argument`.
20. **declaration** — `Foo _field; void M(Foo p){ Foo local; }` target `Foo` → all `declaration`.
21. **attribute** — `class FooAttribute:System.Attribute{} [Foo] class C{}` target `Foo` → `attribute`.
22. **nameof** — `class C{ int _x; string N()=> nameof(_x); }` target `_x` → the nameof occurrence is `nameof`.
23. **xml_doc cref** — `class C{ void M(){} /// <see cref="M"/> void N(){} }` (well-formed cref) target `M` → `xml_doc`. If cref binding is finicky in a bare compilation, this test may need `DocumentationMode.Parse` on the tree — set it and note in a comment.
24. **constructor via type query in new** — target the type `Foo` in `new Foo()` already covered (#10); additionally if a test queries the ctor symbol directly it should also yield `object_creation` (implementer's discretion — one assertion).

**Step 2:** run `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ReferenceClassifier"` → compile FAIL (classifier missing). Correct.

**Step 3: Implement** `src/RoslynCodeLens/Analysis/ReferenceClassifier.cs`. Reference shape (get Roslyn specifics right against the referenced version; this is a sketch, not gospel):

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Analysis;

/// <summary>
/// Classifies a single reference occurrence into a stable kind string. Pure: no I/O.
/// The semantic model is used only to resolve implicit ref/out parameters at call sites.
/// </summary>
public static class ReferenceClassifier
{
    public static string Classify(SyntaxNode node, ISymbol symbol, SemanticModel model)
    {
        // A SimpleName is expected (IdentifierName/GenericName). Fall back gracefully.
        var name = node as SimpleNameSyntax ?? node.FirstAncestorOrSelf<SimpleNameSyntax>();
        if (name == null) return "usage";

        // 1. nameof(...) — the invoked expression is the contextual keyword 'nameof'.
        if (IsInsideNameof(name)) return "nameof";

        // 2. XML doc cref.
        if (name.FirstAncestorOrSelf<XmlCrefAttributeSyntax>() != null
            || name.FirstAncestorOrSelf<CrefSyntax>() != null) return "xml_doc";

        // 3. Constructors and type references route to the type/creation vocabulary.
        var isType = symbol is INamedTypeSymbol or ITypeParameterSymbol;
        var isCtor = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor };
        if (isType || isCtor)
        {
            var creation = name.FirstAncestorOrSelf<BaseObjectCreationExpressionSyntax>();
            if (creation != null && IsInTypeOf(name, creation)) return "object_creation";
            return ClassifyTypeContext(name);
        }

        // 4. Value symbols (locals, fields, properties, params, events, methods).
        return ClassifyValueContext(name, symbol, model);
    }

    private static string ClassifyTypeContext(SimpleNameSyntax name)
    {
        foreach (var anc in name.AncestorsAndSelf())
        {
            switch (anc)
            {
                case AttributeSyntax: return "attribute";
                case BaseObjectCreationExpressionSyntax: return "object_creation";
                case CastExpressionSyntax: return "cast";
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AsExpression): return "cast";
                case IsPatternExpressionSyntax: return "type_check";
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.IsExpression): return "type_check";
                case PatternSyntax: return "type_check";              // declaration/type/recursive patterns
                case TypeOfExpressionSyntax: return "typeof";
                case BaseTypeSyntax: return "base_type";
                case TypeParameterConstraintClauseSyntax: return "type_constraint";
                case TypeConstraintSyntax: return "type_constraint";
                case TypeArgumentListSyntax: return "type_argument";
                case AttributeArgumentSyntax: continue;              // keep climbing
            }
            // Stop climbing at statement/member boundaries.
            if (anc is StatementSyntax or MemberDeclarationSyntax) break;
        }
        return "declaration";
    }

    private static string ClassifyValueContext(SimpleNameSyntax name, ISymbol symbol, SemanticModel model)
    {
        // Effective expression: climb member-access .Name, element access, parenthesized.
        ExpressionSyntax expr = name;
        while (true)
        {
            switch (expr.Parent)
            {
                case MemberAccessExpressionSyntax ma when ma.Name == expr: expr = ma; continue;
                case MemberBindingExpressionSyntax mb when mb.Name == expr: expr = (ExpressionSyntax)mb.Parent!; continue;
                case ElementAccessExpressionSyntax ea when ea.Expression == expr: expr = ea; continue;
                case ParenthesizedExpressionSyntax pe: expr = pe; continue;
            }
            break;
        }

        // Method used as a value vs invoked.
        if (symbol is IMethodSymbol)
        {
            if (expr.Parent is InvocationExpressionSyntax inv && inv.Expression == expr) return "invocation";
            return "method_group";
        }

        switch (expr.Parent)
        {
            case AssignmentExpressionSyntax asn when asn.Left == expr:
                return asn.IsKind(SyntaxKind.SimpleAssignmentExpression) ? "write" : "readwrite";
            case PrefixUnaryExpressionSyntax pre when pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression):
                return "readwrite";
            case PostfixUnaryExpressionSyntax: return "readwrite";
            case ArgumentSyntax arg:
                if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) return "write";
                if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)) return "readwrite";
                // Implicit ref/out from the resolved parameter (rare).
                var rk = ResolveArgRefKind(arg, model);
                return rk switch { RefKind.Out => "write", RefKind.Ref => "readwrite", _ => "read" };
        }
        return "read";
    }

    private static RefKind ResolveArgRefKind(ArgumentSyntax arg, SemanticModel model)
    {
        if (arg.Parent is not BaseArgumentListSyntax list || list.Parent is null) return RefKind.None;
        var invoked = model.GetSymbolInfo(list.Parent).Symbol as IMethodSymbol;
        if (invoked == null) return RefKind.None;
        var index = list.Arguments.IndexOf(arg);
        if (index < 0 || index >= invoked.Parameters.Length) return RefKind.None;
        return invoked.Parameters[index].RefKind;   // NOTE: named args need name matching — handle or accept read.
    }

    private static bool IsInsideNameof(SyntaxNode name)
    {
        for (var anc = name.Parent; anc != null; anc = anc.Parent)
        {
            if (anc is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } } inv
                && inv.ArgumentList.Contains(name))
                return true;
            if (anc is StatementSyntax or MemberDeclarationSyntax) break;
        }
        return false;
    }

    private static bool IsInTypeOf(SyntaxNode name, BaseObjectCreationExpressionSyntax creation)
        => creation is ObjectCreationExpressionSyntax oce && oce.Type.Span.Contains(name.Span);
}
```

Get the Roslyn API names right (`BaseObjectCreationExpressionSyntax`, `MemberBindingExpressionSyntax`, `RefKindKeyword`, `AncestorList.Contains`) against the referenced version; adjust freely. The goal is the 24-case matrix passing. For `nameof`, `ArgumentList.Contains` may not exist — use span containment or `DescendantNodes`.

**Step 4:** filter run → all matrix tests PASS. Debug root causes; don't weaken. If cref (#23) proves unreliable in a bare compilation, keep the code path but mark that test `[Fact(Skip="cref binding requires doc-mode parse")]` with a comment, and note it in the report rather than deleting the classification branch.

**Step 5:** Commit: `feat: ReferenceClassifier — precise reference-kind taxonomy`.

---

### Task 3: Wire into FindReferencesLogic (per-occurrence + column + semantic model cache)

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindReferencesLogic.cs`
- Create/extend: `tests/RoslynCodeLens.Tests/FindReferencesLogicTests.cs`

**Step 1: Failing tests** (via `RenameTestWorkspace`; call `FindReferencesLogic.Execute(loaded, resolver, metadata, symbol)`):

1. `SameLineMultiRef_YieldsTwoItems` — source `class C { int _x; void M(){ _x = _x + 1; } }`, `find_references("C._x")` → at least two items on the `_x = _x + 1` line with distinct `Column`, kinds `write` and `read`. (Old code collapsed to one.)
2. `Write_And_Read_Classified` — the write item precedes/has smaller column than the read; assert both kinds present.
3. `Invocation_Classified` — `find_references("C.M")` for a method → item kind `invocation`.
4. `TypeReference_Kinds` — a type used as base + `new` + `typeof` across a fixture → the returned items carry `base_type`, `object_creation`, `typeof` respectively.
5. `Column_IsOneBased` — a known reference's `Column` equals the 1-based character position.

**Step 2:** run → FAIL (old logic returns per-line, wrong kinds).

**Step 3: Implement.** Replace the classification + dedup in `ScanForReferences`:

- Remove `ClassifyReferenceNode`/`ClassifyReference` (superseded by `ReferenceClassifier`).
- Re-key `seen` to `(string File, int Line, int Column)`.
- Cache semantic models per document: `var models = new Dictionary<DocumentId, SemanticModel>();` obtain via `location.Document.GetSemanticModelAsync().GetAwaiter().GetResult()` once per document.
- Get the referenced symbol from `referencedSymbol.Definition` (pass to the classifier).
- Compute `column = lineSpan.StartLinePosition.Character + 1`, classify, build `SymbolReference` with column.

Sketch of the inner loop:

```csharp
var column = lineSpan.StartLinePosition.Character + 1;
if (!seen.Add((file, line, column))) continue;

var node = sourceTree.GetRoot().FindNode(location.Location.SourceSpan);
if (!models.TryGetValue(location.Document.Id, out var model))
{
    model = location.Document.GetSemanticModelAsync().GetAwaiter().GetResult()!;
    models[location.Document.Id] = model;
}
var kind = ReferenceClassifier.Classify(node, referencedSymbol.Definition, model);
var snippet = GetContainingStatement(node);
var projectName = resolver.GetProjectName(location.Document.Project.Id);
results.Add(new SymbolReference(kind, file, line, column, snippet, projectName, resolver.IsGenerated(file)));
```

Keep `GetContainingStatement` as-is. Ensure the semantic model's tree matches the location's tree (documents in the solution — `GetSemanticModelAsync` returns the right one).

**Step 4:** filter run (`~FindReferencesLogic`) → PASS. Also run `~ReferenceClassifier` still green.

**Step 5:** Commit: `feat: per-occurrence classified references with column`.

---

### Task 4: Tool wrapper — kinds filter + byKind summary

**Files:**
- Modify: `src/RoslynCodeLens/Tools/FindReferencesTool.cs`
- Extend: `tests/RoslynCodeLens.Tests/Tools/FindReferencesToolTests.cs`

**Step 1: Failing tests:**

1. `KindsFilter_NarrowsResults` — `Execute(manager, "C._x", kinds: ["write","readwrite"])` returns only write/readwrite items; `TotalCount` equals the filtered count (not the unfiltered total).
2. `UnknownKind_Throws` — `kinds: ["bogus"]` → `McpToolException` `InvalidArgument`, message/details list valid kinds.
3. `ByKind_SummaryPresent` — summary object has a `byKind` dictionary whose counts match the returned items.
4. Existing tests still green (they don't assert kind strings — verify).

These are MCP-wrapper tests; if the existing FindReferencesToolTests build a manager/fixture, follow that pattern (`[Collection("TestSolution")]`), else exercise via a small helper. Prefer testing the filter/summary logic through the tool `Execute`.

**Step 2:** run → FAIL (param/filter/summary missing).

**Step 3: Implement:**

```csharp
private static readonly IReadOnlySet<string> ValidKinds = new HashSet<string>(StringComparer.Ordinal)
{
    "read","write","readwrite","invocation","method_group","object_creation","cast",
    "type_check","typeof","base_type","type_constraint","type_argument","declaration",
    "attribute","nameof","xml_doc","usage",
};

[McpServerTool(Name = "find_references"),
 Description("Find all references to a symbol (type, method, property, field, or event) across the " +
             "solution, each tagged with a kind. Kinds: `read`, `write`, `readwrite` (compound " +
             "assignment / `++` / `ref`), `invocation`, `method_group`, `object_creation`, `cast`, " +
             "`type_check` (`is` / patterns / `as`-tests), `typeof`, `base_type`, `type_constraint`, " +
             "`type_argument`, `declaration`, `attribute`, `nameof`, `xml_doc`. Pass `kinds` to return " +
             "only some (e.g. `[\"write\",\"readwrite\"]` for mutation sites). Envelope adds a `byKind` " +
             "summary. Multiple references on one line are reported separately with a `column`.")]
public static ToolListResult<SymbolReference> Execute(
    MultiSolutionManager manager,
    [Description("Symbol name: simple type (`MyClass`), fully qualified (`Namespace.MyClass`), or member (`MyClass.MyProperty`)")] string symbol,
    [Description("Optional kind filter — only references of these kinds are returned (see the kind list above)")] string[]? kinds = null,
    [Description("Maximum number of items to return (default: 500). Items are sorted by file, line, column.")] int? limit = null)
{
    manager.EnsureLoaded();

    if (kinds is { Length: > 0 })
    {
        var bad = kinds.Where(k => !ValidKinds.Contains(k)).ToList();
        if (bad.Count > 0)
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                $"Unknown reference kind(s): {string.Join(", ", bad)}.",
                new { validKinds = ValidKinds.OrderBy(k => k, StringComparer.Ordinal).ToArray() });
    }

    var context = manager.GetAnalysisContext();
    var raw = FindReferencesLogic.Execute(context.Loaded, context.Resolver, context.Metadata, symbol);

    if (kinds is { Length: > 0 })
    {
        var set = new HashSet<string>(kinds, StringComparer.Ordinal);
        raw = raw.Where(r => set.Contains(r.ReferenceKind)).ToList();
    }

    var sorted = Sort(raw);
    var summary = BuildSummary(raw);
    return ToolListResult.Create(sorted, limit ?? DefaultLimit, summary);
}

internal static IReadOnlyList<SymbolReference> Sort(IReadOnlyList<SymbolReference> items)
    => items.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ThenBy(r => r.Column).ToList();

internal static object BuildSummary(IReadOnlyList<SymbolReference> items)
{
    var byProject = items.GroupBy(r => r.Project, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    var byKind = items.GroupBy(r => r.ReferenceKind, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    return new { byProject, byKind };
}
```

**Step 4:** filter run (`~FindReferences|~ToolDescriptionMdxSafety`) → PASS.

**Step 5:** Commit: `feat: kinds filter and byKind summary on find_references`.

---

### Task 5: Docs + verification

- SKILL.md (worktree copy):
  - "Response shape" `find_references` summary bullet → `{ byProject: {...}, byKind: {...} }`.
  - Add to the section describing `find_references` a one-liner on the kind vocabulary + the `kinds` filter, and a Red Flags row: `| "Where is this field written / who mutates it?" | \`find_references\` with \`kinds: ["write","readwrite"]\` | ` and `| "Find is/as/pattern-match sites of this type" (would-be find_pattern_usages) | \`find_references\` with \`kinds: ["type_check","cast"]\` |`.
  - Quick Reference row for `find_references` updated to mention kinds.
- CLAUDE.md: tool count stays **60** (enhancement, not a new tool) — do NOT bump.
- docs/BACKLOG.md §5: change the "Reference kind classification on `find_references`" bullet to `✅ **Reference-kind classification on \`find_references\`** — *shipped* (PR #<n>). ...`. It is an enhancement, so do NOT add a Recently-shipped table row; the ✅ inline is enough. (PR number: reference the design doc now; the PR number can be added when known — acceptable to write "(this PR)" and leave it, or update after `gh pr create`.)
- Full `dotnet build` + `dotnet test` (expect ~730+ green). Fixture-pristine check (`git status --short tests/RoslynCodeLens.Tests/Fixtures/` empty).
- Commit: `docs: document find_references reference-kind classification`.

---

## Deviations
Report any Roslyn API mismatch, the cref-test outcome, and named-argument `ref`/`out` handling in the final report; design-relevant items append to the design doc.
