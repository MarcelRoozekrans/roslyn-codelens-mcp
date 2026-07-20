# Exception-Flow Trio — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Three tools — `get_exception_flow`, `find_throw_sites`, `find_catch_blocks` — over a shared pure `ExceptionAnalyzer`, giving the server its first exception analysis.

**Architecture:** `Analysis/ExceptionAnalyzer.cs` holds every primitive (throw-site collection that stops at lambda/local-function boundaries, catch matching, handler lookup within a method, XML `<exception>` parsing). The two site-finders scan all source trees and filter. `get_exception_flow` walks callees depth-bounded (mirroring `GetCallGraphLogic`, but tracking **call-site nodes** so it can test try/catch containment) and propagates each raised exception up the call chain. Design doc: `docs/plans/2026-07-20-exception-flow-design.md` — read it first.

**Tech Stack:** Roslyn (existing deps), xUnit, `RenameTestWorkspace` for isolated tests.

**Working directory:** `.worktrees/exception-flow`, branch `feature/exception-flow`. All commands from its root.

**Conventions:** errors via `McpToolException(ToolErrorCode.X, msg, details)`; list tools use `ToolListResult.Create(items, limit, summary)`; `get_exception_flow` returns a bespoke record like `GetCallGraphResult`; string kinds; tool `[Description]`s must pass `ToolDescriptionMdxSafetyTests` (backtick code tokens, no bare `<`/`{` — note `<exception>` and generics need backticks!); commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`; hooks must pass.

**Reusable context (verified):** `GetCallGraphLogic` builds `treeToCompilation` (`Dictionary<SyntaxTree, Compilation>` from `loaded.Compilations`) — copy that idiom to get a semantic model per tree. Its `EnumerateOutgoingCalls` yields only `(callee, edgeKind)`; the flow walker needs `(callee, callSiteNode)`, so write a variant. `GenerateTestSkeletonLogic.CollectThrownExceptionTypes` (line ~274) is the throw-walk being replaced.

---

### Task 1: Models

**Files:** Create `src/RoslynCodeLens/Models/ThrowSiteInfo.cs`, `CatchBlockInfo.cs`, `ExceptionFlowInfo.cs`, `ExceptionFlowResult.cs`.

```csharp
namespace RoslynCodeLens.Models;

public record ThrowSiteInfo(
    string ExceptionType, string Method, string File, int Line, int Column,
    string Snippet, bool IsRethrow, string Project);
```

```csharp
namespace RoslynCodeLens.Models;

/// <summary>CaughtType is null for a bare `catch`. Rethrows/IsEmpty answer "is this swallowing?".</summary>
public record CatchBlockInfo(
    string? CaughtType, string Method, string File, int Line, int Column,
    bool HasFilter, bool Rethrows, bool IsEmpty, string Snippet, string Project);
```

```csharp
namespace RoslynCodeLens.Models;

/// <summary>
/// One exception that can reach (or be stopped before) the analysed method's boundary.
/// Origin: "thrown" (a real throw site in source) or "documented" (an `exception` XML tag
/// on a metadata symbol). HasFilter means the matching catch has a `when` clause, so it may
/// decline at runtime — such an exception is reported as still escaping.
/// </summary>
public record ExceptionFlowInfo(
    string ExceptionType, string Origin, string RaisedIn, string File, int Line, int Depth,
    IReadOnlyList<string> Path, bool Escapes,
    string? CaughtIn, string? CaughtFile, int? CaughtLine, bool HasFilter);
```

```csharp
namespace RoslynCodeLens.Models;

public record ExceptionFlowResult(
    string Method, int MaxDepthRequested, bool Truncated,
    IReadOnlyList<ExceptionFlowInfo> Exceptions, object Summary);
```

Build (`dotnet build src/RoslynCodeLens`) → 0 errors. Commit: `feat: exception-flow models`.

---

### Task 2: ExceptionAnalyzer (TDD — the primitive matrix)

**Files:** Create `tests/RoslynCodeLens.Tests/ExceptionAnalyzerTests.cs`, `src/RoslynCodeLens/Analysis/ExceptionAnalyzer.cs`.

**Step 1: failing tests.** Helper compiles a source string and returns the analyzer's view of a named method:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests;

public class ExceptionAnalyzerTests
{
    private static (MethodDeclarationSyntax Method, SemanticModel Model) Compile(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create("C", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == methodName);
        return (method, comp.GetSemanticModel(tree));
    }

    private static List<string> ThrownTypes(string source, string methodName)
    {
        var (method, model) = Compile(source, methodName);
        return ExceptionAnalyzer.CollectThrowSites(method, model)
            .Select(s => s.ExceptionType.Name).ToList();
    }
}
```

Tests (each `[Fact]`):

1. `DirectThrow_IsCollected` — `void M() { throw new System.InvalidOperationException(); }` → `["InvalidOperationException"]`.
2. `ThrowExpression_IsCollected` — `string M(string s) => s ?? throw new System.ArgumentNullException();` (use a method body form the helper can find) → `["ArgumentNullException"]`.
3. `ThrowVariable_UsesStaticType` — `void M() { var e = new System.IO.IOException(); throw e; }` → `["IOException"]`.
4. `Rethrow_TakesEnclosingCatchType` — `void M() { try { } catch (System.IO.IOException) { throw; } }` → `["IOException"]`, and the site's `IsRethrow` is true.
5. `BareRethrow_IsException` — `try { } catch { throw; }` → `["Exception"]`.
6. `ThrowInLambda_IsNotCollected` — `void M() { System.Action a = () => throw new System.Exception(); a(); }` → **empty**. (This is the `generate_test_skeleton` bug being fixed.)
7. `ThrowInLocalFunction_IsNotCollected` — `void M() { void Inner() => throw new System.Exception(); Inner(); }` → empty.
8. `CatchesType_ExactAndBase` — for `catch (System.Exception)`, `CatchesType(clause, typeof ArgumentNullException symbol)` → true; for `catch (System.IO.IOException)` vs ArgumentNullException → false.
9. `CatchesType_BareCatch` → true for anything.
10. `FindHandler_CatchesInSameMethod` — `try { throw new System.IO.IOException(); } catch (System.IO.IOException) { }` → handler found, `HasFilter` false.
11. `FindHandler_FilteredCatch_ReportsFilter` — `catch (System.IO.IOException) when (true) { }` → handler found, `HasFilter` true.
12. `FindHandler_FinallyDoesNotCatch` — `try { throw ... } finally { }` → no handler.
13. `FindHandler_ThrowInsideCatchNotCaughtByOwnTry` — `try { } catch (System.Exception) { throw new System.IO.IOException(); }` → no handler (the same try does not protect its own catch body).
14. `FindHandler_StopsAtMethodBoundary` — a throw in a method nested in a class whose *other* method has a try → no handler.
15. `ParseExceptionCrefs_ReadsTags` — pure XML test: `"<member><exception cref=\"T:System.IO.IOException\">no</exception><exception cref=\"T:System.ArgumentNullException\">bad</exception></member>"` → `["System.IO.IOException", "System.ArgumentNullException"]`.

**Step 2:** run `--filter "FullyQualifiedName~ExceptionAnalyzer"` → compile FAIL (type missing). Correct.

**Step 3: implement.** Sketch — verify Roslyn API details against the referenced version:

```csharp
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynCodeLens.Analysis;

/// <summary>One explicit throw found in a method body (not in a nested lambda/local function).</summary>
public sealed record ThrowSite(INamedTypeSymbol ExceptionType, SyntaxNode Node, bool IsRethrow);

/// <summary>A catch clause that would handle an exception, plus whether it may decline.</summary>
public sealed record ExceptionHandler(CatchClauseSyntax Clause, bool HasFilter);

public static class ExceptionAnalyzer
{
    /// <summary>
    /// Explicit throws in this body only. Lambdas, anonymous methods and local functions are
    /// separate execution bodies: a throw inside one escapes when IT runs, not at this
    /// method's boundary, so their subtrees are skipped.
    /// </summary>
    public static IReadOnlyList<ThrowSite> CollectThrowSites(SyntaxNode body, SemanticModel model)
    {
        var sites = new List<ThrowSite>();
        foreach (var node in body.DescendantNodes(descendIntoChildren: DescendInto))
        {
            switch (node)
            {
                case ThrowStatementSyntax { Expression: null } rethrow:
                {
                    var type = EnclosingCatchType(rethrow, model);
                    if (type != null) sites.Add(new ThrowSite(type, rethrow, IsRethrow: true));
                    break;
                }
                case ThrowStatementSyntax { Expression: { } expr } t:
                    Add(t, expr);
                    break;
                case ThrowExpressionSyntax te:
                    Add(te, te.Expression);
                    break;
            }
        }
        return sites;

        void Add(SyntaxNode node, ExpressionSyntax expr)
        {
            if (model.GetTypeInfo(expr).Type is INamedTypeSymbol t)
                sites.Add(new ThrowSite(t, node, IsRethrow: false));
        }
    }

    private static bool DescendInto(SyntaxNode node)
        => node is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax);

    private static INamedTypeSymbol? EnclosingCatchType(SyntaxNode node, SemanticModel model)
    {
        var clause = node.FirstAncestorOrSelf<CatchClauseSyntax>();
        if (clause == null) return null;
        if (clause.Declaration?.Type is { } typeSyntax
            && model.GetSymbolInfo(typeSyntax).Symbol is INamedTypeSymbol declared)
            return declared;
        return model.Compilation.GetTypeByMetadataName("System.Exception");
    }

    public static bool CatchesType(CatchClauseSyntax clause, INamedTypeSymbol exceptionType, SemanticModel model)
    {
        if (clause.Declaration?.Type is not { } typeSyntax) return true;      // bare catch
        if (model.GetSymbolInfo(typeSyntax).Symbol is not INamedTypeSymbol caught) return false;
        for (var t = exceptionType; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, caught)) return true;
        return false;
    }

    /// <summary>
    /// First catch clause protecting this node for this exception type, searching outward but
    /// never past the enclosing method-like body. A node inside a catch or finally block is not
    /// protected by that same try.
    /// </summary>
    public static ExceptionHandler? FindHandler(SyntaxNode node, INamedTypeSymbol exceptionType, SemanticModel model)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (IsBodyBoundary(current)) break;

            if (current.Parent is TryStatementSyntax tryStatement && tryStatement.Block == current)
            {
                foreach (var clause in tryStatement.Catches)
                {
                    if (CatchesType(clause, exceptionType, model))
                        return new ExceptionHandler(clause, clause.Filter != null);
                }
            }
        }
        return null;
    }

    private static bool IsBodyBoundary(SyntaxNode node)
        => node is MemberDeclarationSyntax or LambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax;

    /// <summary>`exception cref` entries from an XML documentation comment.</summary>
    public static IReadOnlyList<string> ParseExceptionCrefs(string? documentationXml)
    {
        if (string.IsNullOrWhiteSpace(documentationXml)) return [];
        try
        {
            return XDocument.Parse(documentationXml).Descendants("exception")
                .Select(e => e.Attribute("cref")?.Value)
                .Where(c => !string.IsNullOrEmpty(c))
                .Select(c => c!.StartsWith("T:", StringComparison.Ordinal) ? c[2..] : c!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (System.Xml.XmlException) { return []; }
    }

    public static IReadOnlyList<INamedTypeSymbol> GetDocumentedExceptions(ISymbol symbol, Compilation compilation)
        => ParseExceptionCrefs(symbol.GetDocumentationCommentXml())
            .Select(compilation.GetTypeByMetadataName)
            .OfType<INamedTypeSymbol>()
            .ToList();
}
```

Note the `FindHandler` loop shape: it must check whether `current` is the *try block* of its parent try. Verify test 13 (throw inside a catch body) passes — if the loop shape is wrong it will falsely find a handler.

**Step 4:** filter run → all 15 PASS. **Step 5:** Commit `feat: ExceptionAnalyzer primitives for exception-flow analysis`.

---

### Task 3: find_throw_sites

**Files:** Create `src/RoslynCodeLens/Tools/FindThrowSitesLogic.cs`, `FindThrowSitesTool.cs`, `tests/RoslynCodeLens.Tests/FindThrowSitesLogicTests.cs`.

**Step 1: failing tests** (via `RenameTestWorkspace`, calling `FindThrowSitesLogic.Execute(loaded, resolver, metadata, exceptionType, includeDerived)`):

Fixture source: a class with `throw new InvalidOperationException()`, `throw new CustomException()` where `class CustomException : InvalidOperationException`, a rethrow in a catch, and a throw inside a lambda.

1. `ExactType_Found` — `"System.InvalidOperationException"` → the direct site only (not the derived one).
2. `IncludeDerived_FindsSubclasses` — same with `includeDerived: true` → both.
3. `Rethrow_IsFlagged` — a `throw;` inside `catch (IOException)` appears when searching IOException, `IsRethrow` true.
4. `LambdaThrow_AttributedToNothing` — a throw inside a lambda still surfaces as a throw site (it IS a throw site in the file) but its `Method` names the enclosing member; assert it appears (this tool scans all throws — unlike the flow tool, which asks what escapes a method). Document that distinction in a comment.
5. `NonExceptionType_Throws` — `"System.String"` → `McpToolException` `InvalidArgument`.
6. `UnknownType_Throws` — → `SymbolNotFound`.
7. `MetadataType_Resolves` — `"System.ArgumentNullException"` resolves and finds source throws of it.

**Step 2:** red run. **Step 3: implement.** Logic outline:

- Resolve the type: `resolver.FindSymbols(name).OfType<INamedTypeSymbol>().FirstOrDefault()` then `metadata.Resolve(name)?.Symbol as INamedTypeSymbol`; null → `SymbolNotFound`.
- Validate it derives from `System.Exception` (walk `BaseType`), else `InvalidArgument`.
- For each compilation, for each syntax tree: get the semantic model, walk **all** `ThrowStatementSyntax`/`ThrowExpressionSyntax` (here we DO include lambdas — a throw site is a throw site), resolve the thrown type the same way `ExceptionAnalyzer` does (reuse its helpers; expose what you need as `internal`/`public`), match exact or (includeDerived) derived-from-target.
- `Method` = nearest enclosing member's display string; `Snippet` via the same trimming style as `FindReferencesLogic.GetContainingStatement`.
- Dedup by (file, line, column) across compilations (a linked file appears in several).

Tool wrapper mirrors `FindReferencesTool`: `[Description]` explains `includeDerived`; envelope with `byType`/`byProject` summary; sorted by file/line/column.

**Step 4/5:** green, commit `feat: find_throw_sites`.

---

### Task 4: find_catch_blocks

**Files:** `src/RoslynCodeLens/Tools/FindCatchBlocksLogic.cs`, `FindCatchBlocksTool.cs`, `tests/RoslynCodeLens.Tests/FindCatchBlocksLogicTests.cs`.

Tests:

1. `ExactType_Found` — `catch (IOException)` found for IOException.
2. `IncludeBaseClauses_FindsBaseAndBare` — with `includeBaseClauses: true`, `catch (Exception)` and bare `catch` also surface for IOException; with it false, they do not.
3. `Filter_IsFlagged` — `catch (IOException) when (...)` → `HasFilter` true.
4. `EmptyCatch_IsSwallow` — `catch (IOException) { }` → `IsEmpty` true, `Rethrows` false.
5. `RethrowingCatch_IsFlagged` — `catch (IOException) { throw; }` → `Rethrows` true, `IsEmpty` false.
6. `BareCatch_HasNullType` — bare `catch` item's `CaughtType` is null.
7. `NonExceptionType_Throws` / `UnknownType_Throws` — as Task 3.

Implementation mirrors Task 3: walk all `CatchClauseSyntax`; match via `ExceptionAnalyzer.CatchesType` when `includeBaseClauses`, else exact symbol equality on the declared type (bare catch excluded unless `includeBaseClauses`). `Rethrows` = the clause's block contains a `ThrowStatementSyntax { Expression: null }` (do not descend into nested lambdas/local functions — reuse the analyzer's descend predicate); `IsEmpty` = block has no statements.

Commit `feat: find_catch_blocks`.

---

### Task 5: get_exception_flow (the hard one)

**Files:** `src/RoslynCodeLens/Tools/GetExceptionFlowLogic.cs`, `GetExceptionFlowTool.cs`, `tests/RoslynCodeLens.Tests/GetExceptionFlowLogicTests.cs`.

**Algorithm** (DFS carrying the call-site chain):

```
Visit(method, depth, chain)          // chain = [(callerMethod, callSiteNode), ...] root→here
  if depth > maxDepth or visited(method) or nodes exhausted: mark truncated; return
  for each throw site S in CollectThrowSites(method body):
      Resolve(S.ExceptionType, raisingNode: S.Node, raisedIn: method, chain)
  if includeDocumented:
      for each callee C of method that is metadata:
          for each documented type T of C:
              Resolve(T, raisingNode: callSiteNode(C), raisedIn: C, chain, origin: "documented")
  for each source callee C with call-site node N (depth+1 <= maxDepth):
      Visit(C, depth+1, chain + [(method, N)])

Resolve(T, raisingNode, raisedIn, chain, origin):
  // 1. handled where it is raised?
  h = FindHandler(raisingNode, T, modelFor(raisingNode))
  if h != null and !h.HasFilter: record caught(at h) ; return
  hasFilter = h?.HasFilter ?? false
  // 2. walk the call chain outward
  for (callerMethod, callSiteNode) in reverse(chain):
      h2 = FindHandler(callSiteNode, T, modelFor(callSiteNode))
      if h2 != null and !h2.HasFilter: record caught(at h2, hasFilter) ; return
      hasFilter |= h2?.HasFilter ?? false
  record escaping(hasFilter)
```

Dedup results by `(ExceptionType, RaisedIn, File, Line)`. `Path` = chain method displays + `raisedIn`. `Depth` = chain length.

**Tests** (RenameTestWorkspace; one fixture with a small call tree):

```csharp
private const string Source = """
    using System;
    namespace Demo;
    public class Svc
    {
        public void Top() { Middle(); }
        public void Guarded() { try { Middle(); } catch (InvalidOperationException) { } }
        public void GuardedFiltered() { try { Middle(); } catch (InvalidOperationException) when (DateTime.Now.Year > 0) { } }
        public void Middle() { Deep(); }
        public void Deep() { throw new InvalidOperationException("boom"); }
        public void Direct() { throw new ArgumentNullException(); }
        public void CaughtLocally() { try { throw new ArgumentNullException(); } catch (ArgumentNullException) { } }
        public void Recursive() { Recursive(); throw new NotSupportedException(); }
    }
    """;
```

1. `DirectThrow_Escapes` — flow of `Svc.Direct` → one item, `ArgumentNullException`, `Escapes` true, `Depth` 0.
2. `CaughtLocally_DoesNotEscape` — `Svc.CaughtLocally` → item with `Escapes` false, `CaughtIn` names the method.
3. `TransitiveThrow_ReachesRoot` — `Svc.Top` with `maxDepth: 3` → `InvalidOperationException` escapes, `Depth` 2, `Path` = `[Top, Middle, Deep]`.
4. `DepthLimit_Truncates` — `Svc.Top` with `maxDepth: 1` → the deep exception is absent and `Truncated` true.
5. `CaughtMidChain_DoesNotEscape` — `Svc.Guarded` → `Escapes` false, caught in `Guarded`.
6. `FilteredCatch_StillEscapes` — `Svc.GuardedFiltered` → `Escapes` true **and** `HasFilter` true.
7. `Recursion_Terminates` — `Svc.Recursive` completes and reports `NotSupportedException` (guards against infinite recursion).
8. `UnknownMethod_Throws` — `SymbolNotFound`.
9. `Summary_CountsEscapingAndCaught` — summary object has correct `escaping`/`caught`.

For documented exceptions: `AdhocWorkspace` references created with a bare `MetadataReference.CreateFromFile` carry **no** XML docs, so `GetDocumentationCommentXml()` returns empty and a documented-exception assertion cannot pass there. Unit-test `ParseExceptionCrefs` (Task 2, test 15) for the parsing, and add ONE fixture-level test against `TestSolutionFixture` (MSBuildWorkspace wires XML docs) asserting that a method calling a documented BCL API surfaces at least one `origin: "documented"` item — if the environment yields none, mark that single test `[Fact(Skip="…")]` with a comment rather than deleting the code path, and report it.

Tool wrapper: bespoke `ExceptionFlowResult`; params `method`, `maxDepth = 3`, `includeDocumented = true`, `maxNodes = 500`.

Commit `feat: get_exception_flow`.

---

### Task 6: Migrate generate_test_skeleton onto the analyzer

**Files:** Modify `src/RoslynCodeLens/Tools/GenerateTestSkeletonLogic.cs` (delete `CollectThrownExceptionTypes`, call `ExceptionAnalyzer.CollectThrowSites` and map to simple type names, preserving first-seen order and dedup).

Run the existing generate_test_skeleton tests: they must stay green. If one pinned the old lambda-descent behaviour (a throw inside a lambda producing an `Assert.Throws` stub), correct it and add a comment naming this migration — that behaviour was the documented bug. Report exactly which tests changed.

Commit `refactor: generate_test_skeleton uses the shared ExceptionAnalyzer`.

---

### Task 7: Docs + verification

- **SKILL.md**: Red Flags rows — `| "What exceptions can escape this method?" / "Is this call safe?" | get_exception_flow |`, `| "Where is X thrown?" | find_throw_sites |`, `| "Who catches X?" / "Is anything swallowing this exception?" | find_catch_blocks with rethrows/isEmpty |`. Add three bullets in an "Exception analysis" area near the code-quality tools, three Quick Reference rows, and three metadata-support rows (flow: source methods + documented metadata callees; site-finders: source scan only, exception type may be metadata).
- **CLAUDE.md**: 60 → **63**.
- **tools/DocGen/Program.cs**: categoryMap entries for all three → `"analysis"`.
- **README.md**: add three tool bullets in the existing style (it lists every tool — check it does before assuming).
- **docs/BACKLOG.md**: §5 exception-flow bullet → ✅ shipped (PR #n); three Recently-shipped rows (`get_exception_flow` / `find_throw_sites` / `find_catch_blocks` | Analysis | #n).
- Full `dotnet build` + `dotnet test` (expect ~850 green — the suite takes ~12 min, run it in the background). Fixture-pristine check.

Commit `docs: document the exception-flow trio (63 tools)`.

---

## Deviations
Report Roslyn API mismatches, the documented-exception test outcome, any generate_test_skeleton test that changed, and anything the `FindHandler` loop shape forced. Design-relevant items append to the design doc.
