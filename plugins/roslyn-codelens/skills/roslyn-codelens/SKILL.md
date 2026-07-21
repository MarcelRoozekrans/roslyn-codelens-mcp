---
name: roslyn-codelens
description: Use when working with any .NET / C# code (.cs/.csproj/.sln/.slnx files), finding callers/references/implementations, checking compiler errors or warnings, running dotnet build for diagnostics, searching for a type/method/interface by name, inspecting DI registrations, detecting dead code or circular dependencies, inspecting source-generator output, or about to Grep/Glob across a C# codebase — activates when roslyn-codelens MCP tools are available.
---

# Roslyn CodeLens — Semantic .NET Intelligence

## Response shape

All list-returning tools wrap their results in an envelope:

```json
{
  "items": [...],
  "totalCount": 142,
  "truncated": false,
  "limit": 500,
  "summary": { ... }
}
```

When `truncated` is `true`, `items` are the **top N by the tool's natural sort order** (severity-first, worst-first, by-project, etc.) — usually that's what you want. Raise `limit` only if the truncated tail matters for the task.

Tools that include a `summary` aggregate:

- `get_diagnostics` — `{ error, warning, info, hidden }` counts
- `find_references` — `{ byProject: { name: count }, byKind: { kind: count } }`
- `find_callers`, `find_attribute_usages` — `{ byProject: { name: count } }`
- `search_symbols`, `find_reflection_usage` — `{ byKind: {...} }`
- `find_unused_symbols` — `{ byKind: {...}, filteredOut: { testMethod, testContainer, mcpTool, generated, composition, interop } }`
- `find_naming_violations` — `{ byRule: {...} }`
- `get_complexity_metrics` — `{ max, avg, overThreshold }`
- `resolve_stack_trace` — `{ byOrigin: { source, metadata, unresolved }, exceptions, skippedFrameLike }` — `skippedFrameLike` counts frame-like-but-unparseable lines (also present in items as `Kind="unknown"`)

Single-object tools (`get_type_overview`, `get_symbol_context`, `apply_code_action`, etc.) are unchanged — they return their bespoke shape directly.

## Error responses

When a tool can't proceed, the response is `isError: true` with content carrying a JSON body of `{ code, message, details? }`. Switch on `code`:

| Code | Meaning | Common source |
|---|---|---|
| `SymbolNotFound` | type/method/property not resolved | `analyze_method`, `get_symbol_context`, `get_type_overview`, `get_type_hierarchy`, `generate_test_skeleton` |
| `SolutionNotTrusted` | analyzers blocked until `trust_solution` is called | `get_diagnostics` (`includeAnalyzers: true`), `get_code_fixes` |
| `AmbiguousMatch` | name matched multiple solutions; `details.matches` lists candidates | `set_active_solution`, `unload_solution` |
| `FileNotFound` | file path / baseline doesn't exist or isn't in solution | `get_file_overview`, `find_breaking_changes` |
| `ProjectNotFound` | solution name didn't match | `set_active_solution`, `unload_solution` |
| `InvalidArgument` | malformed / unsupported caller input | various |
| `Internal` | unexpected; `message` carries exception text | fallback |

If `code: SolutionNotTrusted`, the right next step is calling `trust_solution` after asking the user. Don't catch and retry blindly — the user has to authorize analyzer execution.

## Detection

If `find_implementations` is not available as an MCP tool, this skill is inert — do nothing, no errors.

If it IS available, every rule below applies. No exceptions.

## The Iron Law

**On a .NET codebase where roslyn-codelens MCP tools are available:**

1. **Never** use `Grep`, `Glob`, or Bash `grep`/`rg` to locate C# symbols, types, methods, interfaces, references, callers, implementations, or usages.
2. **Never** run `dotnet build`, `dotnet msbuild`, `msbuild`, or any build command to surface compiler errors, warnings, or analyzer diagnostics.
3. **Never** manually read a `.cs` file to "grep in my head" for who uses a symbol, or to check if code compiles.

The semantic tools (`find_callers`, `find_references`, `find_implementations`, `search_symbols`, `get_diagnostics`, `go_to_definition`, etc.) are **always** more accurate than text search and **always** faster than a build. There is no tradeoff to weigh.

**Violating the letter of these rules is violating the spirit.** If you're about to run a command or tool that *substitutes* for one of these semantic tools, stop.

## Red Flags — STOP and Use the MCP Tool

If any of these thoughts cross your mind, stop and switch to the MCP tool:

| Thought / Action | STOP — Use instead |
|---|---|
| "Let me `Grep` for `class Foo`" | `search_symbols` or `go_to_definition` |
| "Let me `Grep` for `Foo\\.Bar\\(`" (finding callers) | `find_callers` |
| "Who subscribes to this event?" / "Find += sites" / "Are we leaking event subscriptions?" | `find_event_subscribers` |
| "What does this method end up calling?" / "Show me the transitive callees" / "What's the blast radius for changing X?" | `get_call_graph` |
| "Let me `Grep` for `: IFoo`" (finding implementations) | `find_implementations` |
| "Let me `Grep` for `new Foo(`" | `find_references` |
| "Let me `Grep` for `[Authorize]`" | `find_attribute_usages` |
| "What deprecations do we still use?" / "Plan an Obsolete cleanup" / "Find every [Obsolete] call site" | `find_obsolete_usage` |
| "I'll run `dotnet build` to see errors" | `get_diagnostics` |
| "I'll run `dotnet build -warnaserror` to find warnings" | `get_diagnostics` |
| "Let me `Read` the .cs file to see what's defined" | `get_file_overview` or `get_type_overview` |
| "Let me `Glob` for `**/*Service.cs`" | `search_symbols` with a partial name |
| "Let me `Grep` for `Activator.CreateInstance`" | `find_reflection_usage` |
| "I'll check if this method is used by reading files" | `find_callers` / `find_unused_symbols` |
| "What overloads does this method have?" / "Show me all signatures of Foo" / "Compare overloads side-by-side" | `get_overloads` |
| "What can I call on this type?" / "Is there an extension method for X?" / "Does LINQ have something for this?" | `get_extension_methods` |
| "How do I construct this?" / "What constructors does it have?" / "Why can't I `new` this up?" / "Can my test project reach this internal constructor?" | `get_instantiation_options` |
| "What operators does this type define?" / "Does it have a custom `==` or implicit conversion?" / "List `+`, `-`, conversions on this type" | `get_operators` |
| "I'll eyeball complexity by reading the method" | `get_complexity_metrics` |
| "How is this project doing?" / "Where should I focus?" / "Show me the technical debt picture" | `get_project_health` |
| "Which classes are doing too much?" / "Where are my god classes?" / "Worst design smells in this codebase?" | `find_god_objects` |
| "Let me `Grep` for tests that call this method" | `find_tests_for_symbol` |
| "Which tests will break if I change this?" | `find_tests_for_symbol` |
| "What does this test suite cover?" / "List all tests in MyProj.Tests" / "How many xUnit Theory tests do we have?" | `get_test_summary` |
| "What should I write tests for?" / "Where's our testing debt?" / "Show me untested public methods" | `find_uncovered_symbols` |
| "Generate a test stub for this method" / "Bootstrap tests for this class" / "Give me a skeleton I can fill in" | `generate_test_skeleton` |
| "What's the public API of this library?" / "Show me the API surface" / "I need a PublicAPI.txt-style snapshot" | `get_public_api_surface` |
| "Will this break consumers?" / "Show me breaking changes vs the previous release" / "Diff this build's API against baseline" | `find_breaking_changes` |
| "Are there async bugs?" / "Find sync-over-async" / "Are we using `.Result` anywhere?" | `find_async_violations` |
| "Are there resource leaks?" / "Find missing `using`" / "Is this disposable handled?" | `find_disposable_misuse` |
| "What exceptions can escape this method?" / "Is this call safe to make?" / "Do I need a try here?" | `get_exception_flow` |
| "Where is this exception thrown?" / "Who raises `TimeoutException`?" | `find_throw_sites` |
| "Who catches this?" / "Is anything swallowing exceptions?" / "Find empty catch blocks" | `find_catch_blocks` |
| "Rename this class/method everywhere" / "Let me edit N files to change this name" | `rename_symbol` |
| "Add/remove/reorder a parameter" / "Let me update all the call sites by hand" | `change_signature` |
| "Where did this exception come from?" / user pastes a stack trace / "What method is `<M>d__12.MoveNext`?" | `resolve_stack_trace` |
| "Let me `Read` the file to see this method's body" / "Show me the source of these 5 methods" | `get_method_source` |
| "Where is this field written / who mutates it?" / "Is this ever assigned outside the ctor?" | `find_references` with `kinds: ["write","readwrite"]` |
| "Where is this type cast / pattern-matched?" / "Find `is`/`as` sites" | `find_references` with `kinds: ["type_check","cast"]` |

**All of these mean: the MCP tool is the correct tool. Use it.**

## Rationalizations — and why they're wrong

| Excuse | Reality |
|---|---|
| "Just a quick Grep — it's faster." | It isn't. One `find_references` beats iterating Grep + reading matches + deduping false positives. |
| "Grep as a sanity check on top of the MCP tool." | Redundant and misleading. Grep finds comments, strings, partial matches. If Roslyn says there are N references, there are N references. |
| "This is just a string/literal search, so Grep is fine." | If the target is a C# symbol, it's not "just a string" — it has a definition, scope, and binding. Use `find_references`. String literal searches in comments/docstrings are the *only* legitimate Grep case. |
| "`dotnet build` is how everyone checks errors." | Not here. `get_diagnostics` returns compiler errors + analyzer results, structured, without rebuilding. A build is minutes; the tool is milliseconds. |
| "The MCP server might be slow / might fail." | If it fails, report that and ask. Do not silently fall back to Grep — that produces wrong answers that look right. |
| "I need to see the file contents anyway." | `get_file_overview`, `get_type_overview`, and `analyze_method` give you structure without a `Read`. Use `Read` only after you know which lines matter. |
| "The user asked me to Grep." | If the user asked for *a grep* specifically, ask if they actually want semantic results. If they asked for *information about the code* and suggested grep, use the semantic tool and tell them why. |
| "I'm just looking for TODO comments / string literals." | Fine — Grep is legitimate for comments, string literals, and non-C# files. That's the only free pass. |

## Pre-Action Checklist

**Before calling `Grep` or `Glob` on `.cs` / `.csproj` / `.sln` / `.slnx` / `.cshtml` files:**
1. Is the target a C# symbol (type/member/namespace)? → Use `search_symbols` or `find_references`.
2. Is the target an attribute? → Use `find_attribute_usages`.
3. Is the target a reflection pattern? → Use `find_reflection_usage`.
4. Is it *genuinely* a string literal, comment, or non-semantic text? → Grep is OK. State why.

**Before running `dotnet build` / `msbuild` via Bash:**
1. Am I looking for errors, warnings, or analyzer diagnostics? → Use `get_diagnostics`. Stop.
2. Am I actually trying to produce a binary / run tests / package? → Build is OK. State why.

**Before `Read`ing a `.cs` file:**
1. Do I just need structure (what's in it, what's defined)? → `get_file_overview` / `get_type_overview`.
2. Do I need a specific method's shape? → `analyze_method`.
3. Do I need one or more members' actual source bodies? → `get_method_source` (batch-friendly — pass all the names at once).
4. Do I need the actual source lines to edit? → `Read` is OK.

## When to Use Each Tool

### Decision Tree for External Assemblies

```
I want to...                         Tool / Approach
─────────────────────────────────────────────────────────────────────
Work with types in my source code  → existing tools (unchanged)
Look up an external type by name   → go_to_definition / get_symbol_context /
                                     get_type_overview / get_type_hierarchy
                                     (pass fully-qualified name; returns origin="metadata")
Browse what a package exposes      → inspect_external_assembly
Who in my code uses an ext. type?  → find_references / find_callers / find_implementations
See a method's IL bytecode         → peek_il (pass fully-qualified method name with param types)
Inspect an arbitrary DLL           → add a <ProjectReference> to a throwaway
                                     project, rebuild_solution, then use normally
```

### Understanding a Codebase
- `get_project_dependencies` — solution architecture, how projects relate.
- `get_symbol_context` — full context for a type (namespace, base, interfaces, DI deps, public members).
- `get_public_api_surface` — Enumerate every public/protected type and member declared in production projects; deterministically sorted; suitable for API review or breaking-change baselines. Static analysis; only declared (not inherited) members appear.
- `find_breaking_changes` — Diff the current public API surface against a baseline (JSON snapshot from `get_public_api_surface`, or a `.dll` file). Reports Removed/Added/KindChanged/AccessibilityNarrowed/Widened with Breaking/NonBreaking severity. Static analysis; doesn't currently detect return-type, sealed-ness, or nullable changes.
- `get_type_hierarchy` — inheritance chains and extension points.
- `get_type_overview` — one-shot: context + hierarchy + diagnostics (replaces 3 calls).
- `get_file_overview` — types defined in a file + diagnostics, without reading it.
- `analyze_method` — signature + callers + outgoing calls, all in one.
- `get_method_source` — full declaration source (XML docs, attributes, body, original formatting) for one or MANY members by name in a single call: methods (all overloads), constructors (request as `Type.TypeName` — simple or fully qualified; nested types need full qualification), properties, indexers (`Type.this` or `Type.this[]`), fields, events, explicit interface implementations. Per-item statuses (`ok`/`notFound`/`ambiguous`/`metadata`/`unsupportedKind`) — a batch never fails wholesale; `metadata` items carry `Origin` (assembly name/version for `peek_il`) and a `Note` explains non-`ok` outcomes. `StartLine` always matches the first line of `Source`. Whole types are out of scope (`get_type_overview` / `Read`).
- `get_overloads` — Every overload of a method or constructor (source + metadata) with full parameter detail, modifiers, generic type params, XML doc summary, and location. One call instead of N analyze_method calls.
- `get_operators` — every `+`, `-`, `==`, `<`, conversion, etc. on a type, with kind, signature, source location. Includes synthesized record equality. Covers what `get_overloads` excludes.
- `get_extension_methods` — every extension member applicable to a type, from the solution AND referenced assemblies, so LINQ shows up for an `IEnumerable`. Applicability is Roslyn's own (`ReduceExtensionMethod`), so generic inference is exact: `this IEnumerable<T>` applies to `string`, `this IEnumerable<string>` does not. `signature` is the reduced, call-site form — receiver dropped, inference applied, return type first for both kinds (`IEnumerable<int> Where<int>(Func<int, bool>)`, `int Tripled`). It is a signature, not paste-ready source: a partially inferred generic keeps the type parameters still inferred from the arguments (`Select<int, TResult>` — `int` came from the receiver, `TResult` from your lambda). C# 14 `extension` blocks are covered including **properties** and **static methods**, neither of which an `IsExtensionMethod` scan can see. Check `isStatic` before writing the call: `false` (the normal case, including every classic `this` extension, which is declared static but called on an instance) means `value.Doubled()`; `true` means a C# 14 static extension member, called on the type — `int.Zero`. Receivers may be arrays, nullables or tuples (`string[]`, `int?`, `(int, string)`) as well as plain and constructed generic types. Results are **not** filtered by `using` scope — the tool has no call site — so `namespace` is always reported and you may need to add the import. Source extensions sort before metadata ones; `nameFilter` narrows by substring.
- `get_instantiation_options` — how to construct a type, in one call: `constructors` (every parameter's type and name, declared accessibility, `isImplicit`, `isObsolete`), `factories`, `diRegistrations`, and `requiredMembers` you must set in an object initializer. **Read `isImplicit` before concluding a type has no constructor** — a struct, and a class with none declared, both have a usable parameterless one that no source file mentions. `factories` are static members returning the type from ANYWHERE in the solution, which is the case that matters: a type with a private constructor and a separate `WidgetFactory.Create()` looks uninstantiable until you see them. `Task<T>`/`ValueTask<T>` factories are unwrapped and flagged `isAsync`; instance builder methods are excluded because the builder itself needs constructing. Pass `fromProject` to get `accessible` computed from that project's viewpoint, honouring `InternalsVisibleTo` — `accessible: null` means *not computed* (no `fromProject` given), never "inaccessible". For interfaces, abstract and static classes, `instantiable` is false and `note` says why; follow up with `find_implementations`.
- `get_call_graph` — Transitive caller/callee graph for a method, depth-bounded with cycle detection. Adjacency-list output. Use when you need depth > 1 (`analyze_method` is depth=1).

### Navigating Code (**instead of Grep/Glob**)
- `go_to_definition` — jump to the definition.
- `search_symbols` — fuzzy symbol lookup.
- `find_references` — every reference across the solution, each tagged with a `referenceKind` and reported per occurrence (multiple references on one line each get their own item, distinguished by `column`). Pass `kinds` to filter server-side.

**Reference kinds** — `read`, `write` (assignment target, `out` argument), `readwrite` (compound assignment, `++`/`--`, `ref` argument), `invocation`, `method_group` (method used as a delegate, not called), `object_creation`, `cast` (`(T)x`, `x as T`), `type_check` (`x is T`, `is T v`, `case T v:`), `typeof`, `base_type`, `type_constraint`, `type_argument`, `declaration` (variable/parameter/return/field type positions), `attribute`, `nameof`, `xml_doc` (`<see cref=...>`); `usage` is a rare fallback. Note a receiver reads: in `_map[k] = v` the field `_map` is `read` (its contents change, the field isn't reassigned) — same as `_map.Add(k, v)`.

### Finding Dependencies and Usage
- `find_callers` — every call site for a method.
- `find_event_subscribers` — every += / -= site for an event symbol, with resolved handler name and subscribe/unsubscribe tag. Use for UI-event audits or memory-leak hunts (compare subscribe/unsubscribe pairs).
- `find_implementations` — all implementors of an interface / extenders of a class.
- `find_tests_for_symbol` — xUnit/NUnit/MSTest methods that exercise a production symbol; opt-in transitive walk through helpers.
- `get_test_summary` — Per-project inventory of test methods with framework, attribute kind, [InlineData]/[TestCase]/[DataRow] row count, location, and production symbols referenced. Project → tests direction; complements `find_tests_for_symbol` (test → production).
- `find_uncovered_symbols` — Public methods and properties no test transitively reaches (≤ 3 helper hops); sorted by cyclomatic complexity for prioritization. Strict reference-based: an override is not marked covered just because its base or interface declaration is reached — a test calling `IFoo.Bar` does not cover `Foo.Bar`.
- `generate_test_skeleton` — Emit a test-class skeleton (parseable C#) for a method or type. Returns framework, suggested file path, class name, full file contents, and TodoNotes (e.g. constructor dependencies). Stubs cover happy-path Fact, Theory + InlineData for primitive-param methods, `Assert.Throws<T>` per direct-throw exception type, async detection. Tool returns text; agent decides whether to `Write` it. Pairs with `find_uncovered_symbols` (find gap → generate stub).
- `get_di_registrations` — DI wiring and lifetimes.
- `find_reflection_usage` — hidden/dynamic coupling (`Activator.CreateInstance`, `MethodInfo.Invoke`, assembly scanning).
- `get_nuget_dependencies` — NuGet packages and versions.
- `find_attribute_usages` — members decorated with a given attribute.
- `find_obsolete_usage` — Every [Obsolete] call site grouped by deprecation message and severity. Sharper than find_attribute_usages for migration planning. Source AND metadata obsoletes (NuGet) included.

### Diagnostics (**instead of `dotnet build`**)
- `get_diagnostics` — compiler errors, warnings, analyzer diagnostics. Replaces `dotnet build` output.
- `get_code_fixes` — structured edits for a diagnostic.

#### Trust model for analyzer diagnostics

`get_diagnostics` defaults to `includeAnalyzers=false` (compiler diagnostics only). If the user asks for analyzer warnings (StyleCop, Microsoft.CodeAnalysis.Analyzers, CA-prefixed rules, etc.) — OR if you need to call `get_code_fixes` for an analyzer-rule diagnostic:

1. Call `get_diagnostics(includeAnalyzers=true)`.
2. If the server returns an *"untrusted solution"* error: **ask the user before calling `trust_solution`**. Phrase the question to make it clear that analyzer DLLs run as in-process code with the user's privileges. They should only trust solutions they wrote or fully vetted.
3. Prefer `scope="session"` for one-off reviews. Use `scope="persistent"` only when the user confirms they regularly use this solution. Use `scope="addRoot"` to trust an entire directory tree (e.g., `c:\projects\`).
4. Use `list_trusted_paths` to inspect current state when the user asks "is X trusted?". Use `revoke_trust` to drop entries.

Solutions passed on the CLI at server startup are auto-trusted in session scope — `get_diagnostics(includeAnalyzers=true)` against them works without an extra prompt.
- `get_code_actions` — all refactorings/fixes at a position (with optional range).
- `apply_code_action` — execute a refactoring by title. Preview mode by default. Applying refuses to overwrite files that changed on disk since the snapshot (run `rebuild_solution` and retry), and on success the in-memory snapshot updates immediately, so follow-up queries see the new text without waiting for the file watcher. Actions that create a *new* file (extract interface, move type to file) write it, but the new file only enters the snapshot once the watcher picks it up — the result's `warning` says so when it applies.
- `rename_symbol` — solution-wide safe rename of a type or member (Roslyn Renamer; NOT available via `apply_code_action`). Preview by default; new-compiler-error conflicts reported; apply refuses unless `force=true` on: conflicts, a degraded solution load, or files changed on disk since the snapshot (freshness refusal — run `rebuild_solution` and retry; `force` does not bypass freshness). After apply the in-memory snapshot updates immediately — follow-up queries see the new name without waiting for a rebuild. Generic types accept the arity-free qualified form (`Data.Repository` finds `Repository<T>`).
- `change_signature` — add, remove, and reorder a method's parameters, rewriting every call site (Roslyn's change-signature engine; also NOT reachable via `apply_code_action`). `operations` apply in order: `remove` by parameter name, `reorder` with a full permutation of the surviving names, and `add` with a **required** `callSiteValue` naming exactly what each existing call site passes — the tool never guesses that. Give an added parameter a `defaultValue` instead and existing call sites are left untouched. Named arguments, optional parameters, `params` arrays and the extension-method `this` are all handled. `cascadedTo` lists the overrides and interface implementations rewritten alongside it, so check it in preview before applying. Same gates as `rename_symbol`: preview by default, conflict / degraded-load / freshness refusals, `force` to override, immediate snapshot update on apply.
- `resolve_stack_trace` — map a pasted .NET stack trace to file/line/symbol; demangles async/iterator state machines, lambdas, and local functions; handles log-prefixed lines, inner-exception chains, and Demystifier-style traces. Source frames get the declaration site (exact location when the trace carries `in file:line`); external frames resolve with `origin="metadata"` when the assembly is referenced by the solution — frames outside that closure come back `origin="unresolved"`. Frame-like lines that fail all grammars aren't dropped: they surface as `Kind="unknown"` items at their original position, and `summary.skippedFrameLike` counts them. Items stay in trace order.
- `analyze_data_flow` — variable lifecycle over a statement range (declared/read/written/captured/flows-in/out).
- `analyze_control_flow` — reachability, returns, unreachable paths.

**Code generation is in `apply_code_action`** — do NOT look for dedicated generation tools. Use `get_code_actions` to find the title, then `apply_code_action`:
- Implement missing interface/abstract members → *"Implement abstract members"* / *"Implement interface"*
- Generate constructor from fields → *"Generate constructor"*
- Add null checks → *"Add null checks for all parameters"*
- Generate `Equals`/`GetHashCode` → *"Generate Equals and GetHashCode"*
- Encapsulate field → *"Encapsulate field"*
- Extract method → *"Extract method"*
- Inline variable → *"Inline variable"*

### Code Quality Analysis
- `find_unused_symbols` — dead code (reference-based). Auto-filters test methods, MCP tool entry points, source-generator output, MEF-composed services, and interop-laid-out fields; counts surface in `summary.filteredOut`.
- `get_complexity_metrics` — cyclomatic complexity per method.
- `find_naming_violations` — .NET naming conventions.
- `find_async_violations` — Detects sync-over-async (`.Result`/`.Wait()`/`GetAwaiter().GetResult()`), `async void` outside event handlers, missing awaits in async methods, and fire-and-forget tasks. Severity error/warning per violation. Static analysis; no runtime data.
- `find_disposable_misuse` — Detects `IDisposable`/`IAsyncDisposable` instances not wrapped in `using`/`await using`/returned/assigned-to-field-or-out-parameter (warning), and bare-expression-statement discards of a disposable creator/factory (error). Static analysis; no runtime data.

### Exception analysis

- `get_exception_flow` — What can escape a method. Walks callees to `maxDepth` (default 3, cycle-safe) collecting explicit throws, propagates each one up the call chain through every enclosing `try`/`catch`, and reports whether it still escapes the method you asked about — with the propagation `path`, `depth`, and where it was caught. `origin` is `thrown` (a real throw site) or `documented` (an `exception` XML tag on a metadata callee, e.g. the BCL — set `includeDocumented: false` to drop those). Filtered `catch` clauses are skipped rather than accepted, because a `when` filter may decline at runtime — the search continues to the next clause and outward. `hasFilter` describes the reported outcome: it is true only when the exception escaped past a filtered clause that might have caught it, so `escapes: false` always comes with `hasFilter: false`.
- `find_throw_sites` — Every place an exception type is thrown, solution-wide; `includeDerived` also matches subclasses. Rethrows (`throw;`) are flagged and resolved to the enclosing catch's type. Unlike `get_exception_flow`, this counts throws inside lambdas and local functions — they are throw sites, they just don't escape at their enclosing method's boundary.
- `find_catch_blocks` — Every `catch` for a type; `includeBaseClauses` also surfaces `catch (Exception)` and bare `catch`. Each item carries `hasFilter`, `rethrows`, and `isEmpty`, so "what is silently swallowing this?" is one call (`isEmpty: true, rethrows: false`).

**Limits (all three):** static analysis of explicit `throw` only — no runtime-implicit exceptions (null deref, division by zero, OOM), no reflection-invoked throws. Virtual/interface calls follow the declared symbol, not runtime overrides. `get_exception_flow` explores a given callee via the shallowest call path it finds, so a method reached from two call sites at the same depth is analysed via one of them. **Async is modelled as synchronous:** a `throw` inside an `async` method is reported as propagating at the call site, so an enclosing `try` counts as catching it. That is right for `await`ed calls and wrong for fire-and-forget ones (`_ = M();`), where the exception actually surfaces later on another stack and no enclosing `try` sees it.
- `find_large_classes` — oversized types.
- `find_god_objects` — Types crossing all 3 size thresholds (lines/members/fields) AND a coupling threshold (incoming or outgoing namespace count). Sharper than raw size: a large but isolated class isn't flagged; a 200-line class with 15 callers across many namespaces is.
- `find_circular_dependencies` — project/namespace cycles.
- `check_architecture` — enforce layering rules you supply inline. `forbid` (`Domain.*` must not depend on `Infrastructure.*`) catches the violation you know about; `allowOnly` (`Api.*` may depend only on `Application.*`, `Domain.*`) catches the ones you haven't thought of. Edges come from resolved symbols, not `using` directives, so a fully qualified reference with no `using` is still caught and an unused `using` is not reported. **Two semantics you need to read an empty result correctly:** `allowOnly` evaluates only solution-internal, non-generated targets (framework namespaces are ignored — otherwise every file would violate every rule — and so is generator output, which you cannot remove; use `forbid` to restrict either), and self-references are never violations. Results group per violated edge with a full `referenceCount` and the first `maxSitesPerViolation` sites, so one stray cross-boundary reference is one item, not hundreds.
- `get_project_health` — Composite audit: complexity, large classes, naming, unused, reflection, async violations, disposable misuse — counts + top-N hotspots per dimension, per project. Use when you'd otherwise call 7 separate audit tools.

### Source Generators
- `get_source_generators` — list generators and their outputs.
- `get_generated_code` — inspect generated source (filter by generator or file path).

### Working with External Assemblies (Closed-Source / NuGet)

External symbols have `origin.kind = "metadata"` in tool results. Supply fully-qualified names (e.g. `Microsoft.Extensions.DependencyInjection.IServiceCollection`) to the Tier-1 tools below — they fall back to metadata lookup automatically when no source match is found.

- `inspect_external_assembly` — browse a referenced assembly's public API:
  - `mode="summary"` → namespace tree + type counts (start here to orient yourself)
  - `mode="namespace"` → full type + member listing for one namespace
- `peek_il` — read ilasm-style IL for a single method in a referenced assembly:
  - Input: fully-qualified method name with parameter types (e.g. `Namespace.Type.Method(ParamType1, ParamType2)`)
  - For constructors: use `..ctor` notation (e.g. `Namespace.Type..ctor(ParamType)`)
  - Output: raw IL instructions (`IL_0000: ldarg.0`, etc.) — not decompiled C#
  - When to use: after `find_callers` / `get_symbol_context` identifies an interesting external method and you want to understand its implementation

**Worked example — drill into a NuGet package:**
```
inspect_external_assembly(assemblyName: "Microsoft.Extensions.DependencyInjection.Abstractions", mode: "summary")
→ shows NamespaceTree with Microsoft.Extensions.DependencyInjection (15 types)

inspect_external_assembly(assemblyName: "Microsoft.Extensions.DependencyInjection.Abstractions",
    mode: "namespace", namespaceFilter: "Microsoft.Extensions.DependencyInjection")
→ returns IServiceCollection, ServiceDescriptor, ServiceLifetime, etc. with members and XML doc summaries
```

**To find where your code uses an external symbol:**
Use `find_references` / `find_callers` / `find_implementations` with the fully-qualified external symbol name. Results will be source locations in your codebase.

**To find all source files using `IServiceCollection`:**
```
find_references("Microsoft.Extensions.DependencyInjection.IServiceCollection")
→ returns source locations (all origin.kind="source") where your code references IServiceCollection

find_callers("Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton")
→ returns source call sites for AddSingleton (pass the extension class + method name)
```

**To read the raw IL of an external method:**
Use `peek_il` with the fully-qualified method name including parameter types:
```
peek_il("Microsoft.Extensions.DependencyInjection.ServiceDescriptor..ctor(System.Type, System.Type, Microsoft.Extensions.DependencyInjection.ServiceLifetime)")
→ returns ilasm-style IL text, assembly name, and version
```
Limitations: abstract methods, interface instance members, and properties-as-whole (target the getter/setter accessor instead) are not supported.

### Solution Management
- `list_solutions` — loaded solutions, which is active. Includes a `SkippedProjects` array per solution: projects MSBuildWorkspace could not load (legacy non-SDK-style csproj, missing files), with `Kind` and `Reason`.
- `set_active_solution` — switch active solution by partial name.
- `load_solution` — load a `.sln`/`.slnx` at runtime. Returns a confirmation; when projects were skipped, the message summarises them and points to `list_solutions` for per-project reasons. For very large solutions (hundreds of projects) that take minutes to open, pass `background: true` to get a `taskId` back immediately and poll `get_task_status` instead of blocking — the new solution becomes active only once that task succeeds, so other tools keep working against the current solution meanwhile.
- `unload_solution` — free memory.
- `rebuild_solution` — full reload (after `Directory.Build.props` changes, new analyzers/packages, or stale diagnostics).

**Legacy project handling:** when a solution contains non-SDK-style projects (`<Project ToolsVersion="..." xmlns=".../msbuild/2003">`, typically older .NET Framework projects), the server pre-filters them out before MSBuild sees them. Those projects appear in `SkippedProjects` with `Kind: "Legacy"`. The SDK-style projects in the same solution load normally and remain fully queryable — partial load, not all-or-nothing. If a tool returns "no results" for what should be a legacy project, check `list_solutions` first.

### Planning a Change — standard order
1. `get_type_overview` — context + hierarchy + diagnostics.
2. `analyze_change_impact` — blast radius (files, projects, call sites).
3. `find_references` / `find_callers` / `find_implementations` — detailed dependency breakdown.
4. `get_project_dependencies` — architectural position.
5. `get_di_registrations` — wiring.
6. `find_reflection_usage` — dynamic coupling.
7. `find_attribute_usages` — attribute-driven behavior.
8. `get_diagnostics` — existing issues.
9. `get_code_fixes` / `get_code_actions` → `apply_code_action` — auto-fixes and refactorings.
10. `find_unused_symbols` — dead code to delete instead of refactor.
11. `get_complexity_metrics` — complexity hotspots.

Reference concrete types, interfaces, and call sites in your analysis. Not *"the services that implement this"* but *"These 3 classes implement `IUserService`: `UserService`, `CachedUserService`, `AdminUserService`."*

## Tool Quick Reference

| Tool | When to Use |
|------|-------------|
| `find_implementations` | "What implements this interface?" / "What extends this class?" |
| `find_callers` | "Who calls this method?" / "What depends on this?" |
| `find_event_subscribers` | "Who subscribes to this event?" |
| `find_references` | "Where is this symbol used?" / "Show all references" / "Who writes to it?" (`kinds` filter) |
| `find_tests_for_symbol` | "What tests cover this method?" / "Which tests will break if I change X?" |
| `get_test_summary` | "What does this test suite cover?" |
| `find_uncovered_symbols` | "What should I write tests for?" / "Where's our testing debt?" |
| `generate_test_skeleton` | "Generate a test stub for this method" / "Bootstrap tests for this class" |
| `go_to_definition` | "Where is this defined?" / "Jump to source" |
| `search_symbols` | "Find types/methods matching this name" |
| `get_type_hierarchy` | "What's the inheritance chain?" |
| `get_symbol_context` | "Give me everything about this type" |
| `get_public_api_surface` | "What's the public API of this library?" |
| `find_breaking_changes` | "Will this break consumers?" |
| `get_di_registrations` | "How is this wired up?" / "What's the DI lifetime?" |
| `get_project_dependencies` | "How do projects relate?" |
| `get_nuget_dependencies` | "What packages does this project use?" |
| `find_reflection_usage` | "Is this used dynamically?" |
| `find_attribute_usages` | "What's marked [Obsolete]?" / "Find all [Authorize] controllers" |
| `find_obsolete_usage` | "What deprecations do we still use?" |
| `get_diagnostics` | "Any compiler errors?" / "Show warnings" |
| `get_code_fixes` | "How do I fix this warning?" |
| `get_code_actions` | "What refactorings are available here?" |
| `apply_code_action` | "Apply this refactoring" / "Extract method" |
| `rename_symbol` | "Rename this symbol everywhere" / "Change this name across the solution" |
| `change_signature` | "Add/remove/reorder a parameter and fix all the callers" |
| `resolve_stack_trace` | "Where did this exception come from?" / "Resolve this stack trace" |
| `find_unused_symbols` | "Any dead code?" |
| `get_complexity_metrics` | "Which methods are too complex?" |
| `find_naming_violations` | "Check naming conventions" |
| `find_async_violations` | "Are there async bugs?" / "Find sync-over-async" |
| `find_disposable_misuse` | "Are there resource leaks?" / "Find missing `using`" |
| `get_exception_flow` | "What can escape this method?" / "Where does this exception get caught?" |
| `find_throw_sites` | "Where is this exception type thrown?" |
| `find_catch_blocks` | "Who catches this?" / "What's swallowing exceptions?" |
| `find_large_classes` | "Find classes that need splitting" |
| `find_god_objects` | "Which classes are doing too much?" |
| `find_circular_dependencies` | "Any circular dependencies?" |
| `check_architecture` | "Is anything violating our layering?" / "Does Domain reference Infrastructure?" |
| `get_project_health` | "How is this project doing?" / "Top hotspots across all dimensions" |
| `get_source_generators` | "What source generators are active?" |
| `get_generated_code` | "Show generated code" |
| `inspect_external_assembly` | "What does this NuGet package expose?" / "Show me the API of X assembly" |
| `peek_il` | "Show IL for this method" / "What does this external method do at bytecode level?" |
| `list_solutions` | "What solutions are loaded?" |
| `load_solution` | "Load this .sln / .slnx at runtime" |
| `unload_solution` | "Free memory for this solution" |
| `set_active_solution` | "Switch to project B" |
| `rebuild_solution` | "Reload the solution" / "Diagnostics are stale" |
| `start_background_task` | "Kick off a long rebuild without blocking" |
| `get_task_status` | "Check on a queued background task" |
| `list_running_tasks` | "What background work is in flight?" |
| `analyze_data_flow` | "What variables are read/written here?" |
| `analyze_control_flow` | "Is this code reachable?" |
| `analyze_change_impact` | "What breaks if I change this?" (its `directReferenceCount` counts reference *occurrences*, so a line referencing the symbol twice counts twice) |
| `get_type_overview` | "Give me everything about this type in one call" |
| `analyze_method` | "Show signature, callers, and outgoing calls" |
| `get_method_source` | "Show me this method's body" / "Give me the source of these members" |
| `get_overloads` | "What overloads does this method have?" |
| `get_extension_methods` | "What can I call on this type?" / "Is there an extension for X?" |
| `get_instantiation_options` | "How do I construct this?" / "Why can't I `new` this up?" |
| `get_operators` | "What operators does this type define?" |
| `get_call_graph` | "Transitive callers/callees, depth-bounded" |
| `get_file_overview` | "What types are in this file?" |

## Legitimate Grep / Build Exceptions

Grep is the correct tool for:
- Non-C# files (`.json`, `.yaml`, `.md`, `.razor` template text, shell scripts).
- String literals and comments inside C# code.
- Text that isn't a symbol (log messages, error strings, TODOs).

`dotnet build` is the correct command for:
- Actually producing a binary.
- Running tests (`dotnet test`) — not covered by this skill.
- Packaging / publishing.

State the reason when you take the exception. If you're about to type a Grep/Glob/build command and can't state the reason out loud, you're rationalizing — use the MCP tool.

## Metadata Support by Tool

| Tool | Works on metadata symbols | Caveats | Alternative |
|------|--------------------------|---------|-------------|
| `go_to_definition` | Yes — returns `File=""`, `Line=0` with `origin` block | No source location; use to confirm identity | |
| `get_symbol_context` | Yes — members, interfaces, base type | `InjectedDependencies` always empty | |
| `get_type_overview` | Yes | Diagnostics always empty | |
| `get_type_hierarchy` | Yes — base chain from metadata; derived types from source only | Cannot enumerate all ecosystem implementors | |
| `find_attribute_usages` | Yes — resolves metadata attribute type, returns source usages | | |
| `search_symbols` | Yes — includes metadata types (budget heuristic: BCL skipped when source hits exist) | May miss BCL types if source has matches | Use fully-qualified name with `go_to_definition` |
| `find_references` | Yes — finds source usages/references of external symbols | | |
| `find_callers` | Yes — finds source invocations of external methods | | |
| `find_implementations` | Yes — finds source implementors of external interfaces/classes | | |
| `inspect_external_assembly` | Metadata only — this is its purpose | Assembly must be referenced by a project in the solution | `get_nuget_dependencies` to discover assembly names |
| `peek_il` | Metadata only — this is its purpose | Abstract methods and interface instance members not supported | Use `go_to_definition` to confirm the method exists first |
| `get_diagnostics` | No — source only | | |
| `get_code_fixes` | No — source only | | |
| `get_code_actions` | No — source only | | |
| `apply_code_action` | No — source only | | |
| `rename_symbol` | No — source only | Locals/parameters and file renames unsupported | |
| `change_signature` | No — source only; a metadata-defined method is refused | Reflection / `dynamic` call sites are not rewritten | |
| `resolve_stack_trace` | Yes — external frames resolve when the assembly is referenced by the solution | Metadata frames carry no file/line; frames outside the solution's reference closure come back `origin="unresolved"` | `peek_il` to inspect external method bodies |
| `analyze_data_flow` | No — source only | | |
| `analyze_control_flow` | No — source only | | |
| `analyze_change_impact` | No — source only | | |
| `analyze_method` | No — source only | | |
| `get_method_source` | No — source only; metadata members reported with status `"metadata"` | | `peek_il` / `inspect_external_assembly` for external bodies |
| `get_file_overview` | No — source only | | |
| `find_unused_symbols` | No — source only | | |
| `get_complexity_metrics` | No — source only | | |
| `find_naming_violations` | No — source only | | |
| `find_large_classes` | No — source only | | |
| `find_circular_dependencies` | No — source only | | |
| `check_architecture` | Source scan; `forbid` may target metadata namespaces | `allowOnly` ignores metadata and generated targets by design | |
| `get_source_generators` | No — source only | | |
| `get_generated_code` | No — source only | | |
| `get_di_registrations` | No — source only | | |
| `get_nuget_dependencies` | Partial — lists referenced packages, not assemblies directly | Use `inspect_external_assembly` for assembly API | |
| `find_reflection_usage` | No — source only | | |
| `find_async_violations` | No — source only | | |
| `find_disposable_misuse` | No — source only | | |
| `get_exception_flow` | Partial — source methods are analysed; metadata callees contribute their documented exceptions | Documented items are the library's XML docs, not analysis | `includeDocumented: false` to exclude |
| `find_throw_sites` | Source scan — the exception *type* may be a metadata type | Only explicit `throw`, never runtime-implicit exceptions | |
| `find_catch_blocks` | Source scan — the exception *type* may be a metadata type | | |
| `find_obsolete_usage` | No — source only — but `[Obsolete]` source/metadata attribute type both detected | | |
| `find_god_objects` | No — source only | | |
| `find_breaking_changes` | Partial — current side is source; baseline can be a `.dll` from metadata | | |
| `find_event_subscribers` | Yes — accepts metadata event symbols (e.g. `System.Diagnostics.Process.Exited`); reports source `+=`/`-=` sites | | |
| `find_tests_for_symbol` | No — production symbol must be source (test methods are source too) | | |
| `find_uncovered_symbols` | No — source only | | |
| `generate_test_skeleton` | No — source only | | |
| `get_call_graph` | No — source only | | |
| `get_overloads` | Yes — source + metadata overloads, full parameter detail | | |
| `get_extension_methods` | Yes — reports both source and metadata (BCL/NuGet) extensions | Not filtered by `using` scope. For a type the solution DECLARES, an extension in a project that project doesn't reference is excluded; for a metadata receiver (`string`, `int`) no project declares it, so every project's extensions on it are reported | |
| `get_operators` | Yes — source + metadata operators and conversions | | |
| `get_project_dependencies` | N/A — project-level graph | | |
| `get_project_health` | No — source only | | |
| `get_public_api_surface` | No — source only (production projects) | | Use `inspect_external_assembly` for external assembly surfaces |
| `get_test_summary` | No — source only (test projects) | | |
| `list_solutions` | N/A | | |
| `set_active_solution` | N/A | | |
| `load_solution` | N/A | | |
| `unload_solution` | N/A | | |
| `rebuild_solution` | N/A | | |
| `trust_solution` | N/A — security/lifecycle | | |
| `list_trusted_paths` | N/A — security/lifecycle | | |
| `revoke_trust` | N/A — security/lifecycle | | |
