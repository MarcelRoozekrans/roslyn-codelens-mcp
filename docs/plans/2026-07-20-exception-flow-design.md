# Exception-Flow Trio — Design

Date: 2026-07-20
Status: Approved
Origin: SharpLensMcp gap analysis (docs/BACKLOG.md §5, high value #5 — the last one). We have zero exception analysis today. Three tools: `get_exception_flow`, `find_throw_sites`, `find_catch_blocks`. Tool count 60 → 63.

## Decisions (brainstorming outcomes)

| Question | Decision |
|---|---|
| `get_exception_flow` depth | Depth-bounded callee walk (cycle detection + node cap, mirroring `get_call_graph`), `maxDepth` default **3**. Direct-only would be near-trivial; unbounded explodes. |
| Metadata/BCL calls | Read `<exception cref="T">` from XML docs of metadata symbols, reported with `origin: "documented"` (vs `"thrown"` for a real source throw site). Never conflated. Source methods are analysed, not read from docs. |
| Tool shape | **Three** tools — different inputs (a method vs an exception type), different result shapes. Unlike the `get_method_source` merge, these aren't the same question at different cardinality. |
| Catch model | A matching clause marks the exception `caught`; if the clause has a `when` filter it also carries `hasFilter: true` and still counts as escaping, because a filter may decline. Bare `catch` and `catch (Exception)` catch everything; `finally` never catches. |
| Shared core | New `ExceptionAnalyzer`; `generate_test_skeleton`'s private throw-walk migrates onto it (fixing its known lambda-descent bug). |

## Shared core — `Analysis/ExceptionAnalyzer.cs` (pure, no I/O)

- **`CollectThrowSites(SyntaxNode body, SemanticModel model)`** → throw sites within *this* method body:
  - `throw new T(...)` → `T` from `GetTypeInfo`.
  - `throw expr;` (variable, field, factory call) → static type of `expr`.
  - `throw;` → the enclosing `CatchClauseSyntax`'s declared type (bare enclosing catch → `System.Exception`), flagged `isRethrow`.
  - **Does not descend into lambdas, anonymous methods, or local functions.** A throw inside a lambda escapes when the lambda runs, not at the enclosing method's boundary. (This is the documented `generate_test_skeleton` bug being fixed.)
- **`CatchesType(CatchClauseSyntax clause, INamedTypeSymbol exceptionType, SemanticModel model)`** → bare catch ⇒ true; declared type equal to, or a base of, `exceptionType` ⇒ true.
- **`FindHandler(SyntaxNode raisingNode, INamedTypeSymbol exceptionType, SemanticModel model)`** → walks enclosing `TryStatementSyntax`es outward *within one method*; returns the first matching catch clause (with its `hasFilter`), or none. A node inside a `catch` or `finally` block is not protected by that same `try`.
- **`GetDocumentedExceptions(ISymbol symbol, Compilation compilation)`** → `<exception cref="...">` entries parsed from the symbol's XML doc, resolved to type symbols where possible.

## `get_exception_flow`

```
get_exception_flow(method: string, maxDepth: int = 3, includeDocumented: bool = true, maxNodes: int = 500)
```

Algorithm: resolve the method; walk callees depth-first to `maxDepth` (visited-set cycle detection, `maxNodes` cap). At each visited method collect throw sites; for metadata callees collect documented exceptions when `includeDocumented`. Then propagate each raised exception upward along its call path: it escapes the method that raised it unless `FindHandler` matches there; if it escapes, test the *call site* one level up against that caller's try/catch; repeat to the root. An exception still unhandled at the root escapes.

Bespoke result (following `get_call_graph`'s precedent rather than the list envelope, because the root carries metadata):

`ExceptionFlowResult { Method, MaxDepthRequested, Truncated, Exceptions: ExceptionFlowInfo[], Summary }`
`ExceptionFlowInfo { ExceptionType, Origin ("thrown"|"documented"), RaisedIn (method display), File, Line, Depth, Path (method displays root→raiser), Escapes, CaughtIn?, CaughtFile?, CaughtLine?, HasFilter }`
Summary: `{ escaping, caught, byType: { type: count } }`.

## `find_throw_sites`

```
find_throw_sites(exceptionType: string, includeDerived: bool = false, limit: int = 500)
```

Solution-wide scan of every source tree's throw statements/expressions; match when the thrown type equals the target, or (with `includeDerived`) derives from it. Standard list envelope, sorted by file/line/column.
`ThrowSiteInfo { ExceptionType, Method, File, Line, Column, Snippet, IsRethrow, Project }`; summary `{ byType, byProject }`.

## `find_catch_blocks`

```
find_catch_blocks(exceptionType: string, includeBaseClauses: bool = false, limit: int = 500)
```

Scan every `catch` clause; match when the declared type equals the target, or (with `includeBaseClauses`) is a base of it — so `catch (Exception)` and bare `catch` surface as handlers of the requested type. Standard list envelope.
`CatchBlockInfo { CaughtType (null for bare catch), Method, File, Line, Column, HasFilter, Rethrows, IsEmpty, Snippet, Project }`.
`Rethrows`/`IsEmpty` make "who silently swallows this?" a single call — a primary reason to reach for the tool.

## Resolution & errors

Exception types resolve through `SymbolResolver` then `MetadataSymbolResolver`, so `System.ArgumentNullException` works. A type that does not derive from `System.Exception` → `InvalidArgument` (with the resolved display name); unresolvable → `SymbolNotFound`; unresolvable method for the flow tool → `SymbolNotFound`. `EnsureLoaded()` as usual.

## Known limits (documented, not bugs)

Static analysis only: no runtime paths, no reflection-invoked throws, no exceptions from implicit operations (null derefs, division by zero, OOM) — only explicit `throw` and documented metadata exceptions. Virtual/interface dispatch follows the declared symbol, not runtime overrides. `maxDepth` truncation is reported via `Truncated`.

**First-call-site-wins:** the walk's visited-set means a callee reached from two different call sites is explored via the first one only, so an exception's caught/escaping verdict reflects that path. Consistent with `get_call_graph`'s cycle handling. A method called both inside and outside a `try` may therefore be reported either way; re-run the flow on the specific caller to disambiguate.

## Testing

`ExceptionAnalyzerTests` matrix (RenameTestWorkspace): direct throw, `throw expr`, `throw ex` rethrow-by-variable, `throw;` in catch (type inferred from the clause), throw in a lambda **not** attributed to the enclosing method, throw in a local function likewise, caught by exact type / base type / bare catch / `catch (Exception)`, filtered catch flagged, `finally` doesn't catch, throw inside a catch block not caught by its own try. Flow tests: depth-1/2/3 propagation, caught mid-chain, cycle safety, `maxNodes` truncation, documented BCL exception via `includeDocumented`, `Path` correctness. Site/catch tool tests: `includeDerived` on/off, `includeBaseClauses` on/off, rethrow flag, swallow detection (`isEmpty`/`rethrows`), non-exception type → `InvalidArgument`. Fixture integration on TestSolution. `generate_test_skeleton` tests must stay green through the migration — except any that pinned the lambda-descent bug, which get corrected with a comment.

## Docs

SKILL.md: three Red Flags rows ("what can this method throw?", "where is X thrown?", "who catches/swallows X?"), tool bullets, Quick Reference rows, metadata-support rows (flow: source methods, documented metadata; site-finders: source scan). CLAUDE.md 60 → **63**. `tools/DocGen/Program.cs` categoryMap: all three → `analysis`. BACKLOG §5: mark the trio ✅ shipped + three Recently-shipped rows.
