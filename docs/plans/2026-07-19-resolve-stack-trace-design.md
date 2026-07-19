# `resolve_stack_trace` — Design

Date: 2026-07-19
Status: Approved
Origin: SharpLensMcp gap analysis (docs/BACKLOG.md §5, high value #2). Maps a pasted .NET runtime stack trace to file/line/symbol against the loaded solution, undoing compiler name mangling — the debugging-workflow tool nothing in the current 58 covers.

## Decisions (brainstorming outcomes)

| Question | Decision |
|---|---|
| Input scope | Standard `Exception.ToString()` traces AND log-embedded/aggregate forms: lines prefixed by timestamps/log levels (parser anchors on `at `), inner-exception chains (`---> `), AggregateException flattening, and Ben.Demystifier-style prettified frames (`at async Task<int> Ns.T.M(...)`). Unparseable noise lines are dropped. |
| Location for frames without `in file:line` | Declaration site of the resolved method (file + line from the loaded solution). No `LineKind` field (YAGNI — declined). Frames carrying `in file:line` keep that exact location. |
| External (BCL/NuGet) frames | Resolve via `MetadataSymbolResolver` and mark `origin="metadata"` (no file/line). Unresolvable frames return `origin="unresolved"` with the parsed pieces intact. |
| Implementation | Approach A: pure text parsing + demangling + existing resolver infrastructure. Rejected: parsing-library dependency (text-demangling isn't in any library; Ben.Demystifier needs live `StackTrace` objects); PDB/IL mapping (offsets aren't in trace text; declined with the declaration-site decision). |

## Tool contract

```
resolve_stack_trace(
  stackTrace: string,   // pasted trace text, any of the supported forms
  limit?: int           // standard envelope limit (default 500)
)
```

Returns the standard list envelope. **Items keep original trace order — never re-sorted.** `summary: { byOrigin: { source, metadata, unresolved }, exceptions: <count> }`.

## Frame model — `StackFrameInfo`

| Field | Meaning |
|---|---|
| `Index` | 0-based position in the returned sequence |
| `Raw` | trimmed original line |
| `Kind` | `exception` \| `method` \| `asyncMethod` \| `iterator` \| `lambda` \| `localFunction` \| `constructor` \| `unknown` |
| `Symbol` | demangled display (`OrderService.ProcessAsync`; exception type name for `Kind=exception`) |
| `EnclosingMethod` | for lambdas/local functions: the user-written method containing them; null otherwise |
| `File`, `Line` | exact location when the frame carried `in file:line`; declaration site otherwise; null for metadata/unresolved |
| `Origin` | `source` \| `metadata` \| `unresolved` |
| `Project` | project name for source frames |

Exception header lines (`System.Xyz: message`, including each `---> ` inner exception) become `Kind=exception` items so the chain structure is visible. `--- End of stack trace from previous location ---` separators are dropped.

## Parsing

Per line: strip any prefix before the `at ` anchor (timestamps, log levels, indentation); detect exception headers (line contains a type-name-shaped token followed by `: `; `---> ` marks inner). Two frame grammars:

1. Runtime: `at <method-part>(<params>)[ in <file>:line <N>]`
2. Demystifier: `at [async ]<return-type> <method-part>(<params>)` — recognized by the return-type token before the method part; demystified names are already unmangled, so they skip demangling and go straight to resolution.

`<method-part>` splits on the last `.` outside brackets; nested types use `+`; generic arity appears as `` `N `` on types and `[T,U]` on methods.

## Demangling rules (runtime grammar only)

| Pattern | Meaning | Resolution |
|---|---|---|
| `Ns.T+<M>d__12.MoveNext` | async or iterator state machine of `Ns.T.M` | resolve `M`; `Kind` = `asyncMethod` if symbol `IsAsync`, `iterator` if return type is `IEnumerable/IEnumerator/IAsyncEnumerable` variant, else `method` |
| `Ns.T+<>c.<M>b__5_0` / `Ns.T+<>c__DisplayClass5_0.<M>b__1` | lambda declared in `Ns.T.M` | `Kind=lambda`, `Symbol=Ns.T.M` scope target, `EnclosingMethod=M`; location = `M`'s declaration |
| `Ns.T.<M>g__Name|5_0` | local function `Name` in `Ns.T.M` | `Kind=localFunction`, `EnclosingMethod=M`; resolve `M` for location |
| `.ctor` / `.cctor` | constructor / static constructor | `Kind=constructor`; resolve `T`'s ctor |
| `` T`1 ``, `M[T]` | generic arity | strip for lookup (arity-stripped index from PR #300 does the type side) |

Resolution order per frame: solution source (exact `_typesByFullName` → arity-stripped index) → `MetadataSymbolResolver` → `unresolved`. Overloads: choose by parsed parameter count; tie or no params parsed → first match. Frames with `in file:line` keep the trace's location even when symbol resolution fails (origin still reflects the symbol outcome).

## Error handling

Zero recognizable frames in the input → `InvalidArgument` ("no stack frames recognized in input"). Standard envelope and error codes otherwise; no new codes; `manager.EnsureLoaded()` as usual.

## Testing

- **Parser/demangler unit matrix** (pure functions, no workspace): every mangled form above, log-prefixed lines, `---> ` chains, AggregateException text, Demystifier samples, separators, noise lines, malformed input.
- **Resolution tests** on `RenameTestWorkspace`: async method, lambda (both `<>c` and display-class forms), local function, generic type (`` `1 `` + arity-stripped lookup), nested type (`+`), constructor, overload picked by param count, frame with explicit `in file:line`.
- **Metadata frame**: `System.String.Concat` resolves with `origin=metadata`.
- **Integration**: one realistic multi-frame trace (source + metadata + noise) against the TestSolution fixture.

## Docs follow-ups

- SKILL.md: Red Flags row ("Where did this exception come from?" / pasting a stack trace → `resolve_stack_trace`), tool bullet under Understanding/Diagnostics, Quick Reference row, metadata-support row (`Yes — resolves both source and metadata frames`).
- CLAUDE.md tool count 58 → 59; BACKLOG.md §5 bullet → shipped on merge, Recently shipped row.
