# resolve_stack_trace Hardening (#303 + post-merge review findings)

Fixes issue #303 plus the 10 findings from the post-merge high-effort review of PR #301. Branch `fix/303-stack-trace-hardening`.

**Empirical ground truth** (verified on net10 with real `Exception.ToString()` output — these are the authoritative test fixtures):

```
at Program.<>c.<<Case1_AsyncLambda>b__8_0>d.MoveNext()                                  ← awaited async lambda: DOT nesting, '>d' suffix
at Program.<Case2_CapturingLocalFunction>g__Boom|9_0(<>c__DisplayClass9_0&)             ← capturing local fn: hosted on the type, display STRUCT byref param
at Program.<>c__DisplayClass10_0.<Case2b_CapturingLocalFunctionWithLambda>g__Boom|1()   ← local fn sharing captures with a lambda: DOT-nested display CLASS, single-index |1
System.InvalidOperationException: outer
 ---> System.ArgumentNullException: Value cannot be null. (Parameter 'inner')           ← modern .NET: '--->' on OWN line (handled today)
at ThrowingStatics..cctor()                                                             ← static ctor frame
at Program.GenericThrow[T](T x) in ...:line 71                                          ← modern runtime AUTO-demangles ordinary/generic async methods
```

Key insight: modern .NET prints nested types with `.` (never `+`) and auto-demangles ordinary async frames, so the mangled forms that actually reach the tool from modern traces are async lambdas (`<<M>b__N>d`), async local functions (`<<M>g__Name|N>d`), and dot-nested display classes. `+`-nested `d__N` forms come from .NET Framework traces, `Environment.StackTrace`, and old logs — still in scope, no longer the main case.

## W1 — Parser + demangler

1. **Dot-nesting (review #1)**: stop assuming `+`. Normalize the type portion once (shared helper): strip generic-instantiation blocks (`[[...]]` and `[...]` suffixes), strip backtick arity, convert `+`→`.`; then treat mangled containers as trailing dotted segments. Keep the ORIGINAL parsed type text on `DemangledTarget` (new field `RuntimeTypeName`, instantiation blocks removed but `+`/backticks preserved) for W2's metadata fallback.
2. **New state-machine forms (review #2)**: with method `MoveNext`, the last segment may be `<M>d__N` (classic → StateMachine), `<<M>b__N_M>d(__N)?` (async lambda → Lambda, enclosing M), or `<<M>g__Name|N(_M)?>d(__N)?` (async local function → LocalFunction, enclosing M, name Name). After stripping the mangled segment, also strip a now-trailing `<>c` / `<>c__DisplayClassN(_M)?` container segment. Non-async lambda (`<M>b__N`) and local-function (`<M>g__Name|N`, single- or double-index suffix per Case2b) methods likewise strip trailing container segments.
3. **Demystifier grammar (review #3)**: replace the last-space heuristic + single-parens regex with a parser that (a) finds the parameter list as the LAST balanced `(...)` group on the line, tolerating nested parens (ValueTuple) and a trailing ` + 0xNN` offset; (b) takes the method path as the identifier chain immediately before that group, so return types with spaces/generics/tuples don't truncate it; (c) recognizes Demystifier's `+LocalFunc(...)` / `+(...) => { }` suffixes — map to localFunction/lambda with the enclosing method, rather than dropping the line.
4. **AOT offset (#303)**: runtime grammar tolerates ` + 0x[0-9a-f]+` after the parameter list (frame still resolves; offset discarded).
5. **skippedFrameLike (#303)**: `Parse` returns `StackTraceParseResult(IReadOnlyList<ParsedTraceLine> Lines, IReadOnlyList<string> FrameLikeUnparsed)` — lines containing the `at ` anchor that fail all grammars are collected instead of silently dropped. The logic emits them as items (`Kind="unknown"`, `Origin="unresolved"`, `Symbol`=trimmed line) so trace structure stays complete, and the tool summary gains `skippedFrameLike` = their count.
6. **Same-line inner headers (review #8)**: a header line containing ` ---> ` splits into one header entry per exception (Framework format `Outer: msg ---> Inner: msg`).
7. **Generic exception headers (review #6)**: header type charclass admits `[`/`]` instantiation blocks; the emitted TypeFullName has instantiation blocks + arity stripped via the shared normalizer.
8. **Bare-input fallback**: if zero lines are recognized AND the input is a single line, retry it as a frame body (optional `at `, appending `()` if no parameter list) before throwing InvalidArgument — makes the SKILL's "What method is `<M>d__12.MoveNext`?" routing actually work.

## W2 — Resolution logic, DocGen, tests

9. **Top-level comma counting (review #4)**: parameter count counts commas only at bracket depth 0 (tracking `<>`, `[]`, `()`), not `Split(',')`.
10. **`.cctor` (review #5)**: constructor resolution uses `InstanceConstructors` for `.ctor` and `StaticConstructors` for `.cctor`; the member-form metadata fallback is skipped for constructor frames (the `T..ctor` name can never match).
11. **Metadata name form (review #7)**: for the metadata fallback, try `RuntimeTypeName` (original `+`/backtick form) first — that's what `GetTypeByMetadataName` speaks — then the display-normalized form; member form then type-only, unchanged order otherwise.
12. **Unused parameter**: `ResolveStackTraceLogic.Execute` drops the never-read `LoadedSolution` parameter (3 call sites).
13. **DocGen hardening (review #10)**: if `EscapeMdx` ends with `inCode == true` (unbalanced backticks), fall back to escaping the whole string ignoring code spans — an unpaired backtick can no longer disable escaping. Plus a new test in RoslynCodeLens.Tests that reflects over every `[McpServerTool]` method's `[Description]` (tool + parameters) asserting balanced backticks and no bare `<`/`{` outside code spans — the authoring-time gate that keeps docs CI green.

## Docs (review #9)

- SKILL.md: metadata claim becomes "external frames resolve with `origin=\"metadata\"` when the assembly is referenced by the solution; frames outside that closure come back `origin=\"unresolved\"`"; note the new `skippedFrameLike` summary field and unknown-frame items.
- BACKLOG.md: shipped bullet cites PR #301 (not the deleted branch); `resolve_stack_trace | Navigation | #301` row added to Recently shipped.
- README.md: tool list gains rename_symbol and resolve_stack_trace (verify the list's format first).

## Testing

All existing 700 tests stay green (parser/demangler behavior for previously-passing forms unchanged). New tests pin every item above, with the empirical trace lines quoted verbatim as fixtures — especially Case1/Case2b dot-nested forms resolving to source. `StackTraceParseResult` migration updates existing parser tests mechanically.
