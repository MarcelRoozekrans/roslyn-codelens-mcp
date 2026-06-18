# Backlog

Ideas for future tools, grouped by theme. Not committed work — captured here so they're not lost. Each entry is a starting point for a real design discussion.

Last refreshed: 2026-05-04.

---

## 1. Async & concurrency tools

Common .NET pain points that aren't covered by analyzers everyone has on by default.

- **`find_thread_safety_issues`** — lock usage patterns, shared mutable state in static fields, captured locals in tasks. *Note: deep heuristic territory; design carefully or punt.*

## 2. Navigation niceties

Small, focused queries that aren't currently expressible in one call.

- **`find_duplicated_code`** — heuristic detection of repeated statement blocks across files. *Note: existing tools (JSCPD, SonarQube) cover this well; only worth doing if a Roslyn-semantic angle is identified.*

## 3. Generation & scaffolding (write-side)

Companions to `apply_code_action`, but for shapes that Roslyn doesn't ship out of the box.

- **`generate_dto_from_class`** — given a domain class, emit a DTO + AutoMapper-style mapping (or manual `ToDto`/`FromDto` extension methods). *Note: opinionated — picking a mapping style is the hard part.*
- **`generate_builder`** — fluent builder for a class, including required-property tracking.

## 4. Startup & loading performance

Big-solution scenarios (400+ projects) where the structural open dominates wall-clock and blocks the client agent. The project-filter feature (issue [#232](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/232)) is the in-flight first step; the items below are deferred companions.

- **Parallelise the per-project fallback loader** — `SolutionLoader.OpenPerProjectAsync` currently iterates `foreach … await workspace.OpenProjectAsync(entry.Path)` sequentially. With 400 projects even an unfiltered load would benefit. *Note: `MSBuildWorkspace` is documented as not fully thread-safe for opens; needs validation (probably one workspace per worker, then re-stitch). Promote once the filter feature ships and we have measurements.*
- ✅ **Async `load_solution` with a load handle** — *shipped.* `load_solution` gained a `background: true` flag: it runs the load on the existing `BackgroundTaskStore` and returns a `taskId` immediately; the agent polls `get_task_status`. Turned out far smaller than feared — no `SolutionManager`/`EnsureLoaded` changes, because the new solution only becomes active once the background load finishes, so other tools never block on it. Reuses the background-task infra (which postdates this note) rather than a bespoke `get_load_status`. Design: [docs/plans/2026-06-18-async-load-solution-design.md](plans/2026-06-18-async-load-solution-design.md).

---

## In flight

Active branches with no merged PR yet.

_(none currently)_

---

## Recently shipped

Items previously in this backlog, now merged. Listed for orientation; do not re-design without confirming.

| Tool | Theme | PR |
|---|---|---|
| `find_tests_for_symbol` | Test-aware | #116 |
| `find_uncovered_symbols` | Test-aware | #124 |
| `get_test_summary` | Test-aware | #152 |
| `find_async_violations` | Async & concurrency | #126 |
| `find_disposable_misuse` | Async & concurrency | #129 |
| `get_public_api_surface` | API surface | #132 |
| `find_breaking_changes` | API surface | #134 |
| `find_obsolete_usage` | API surface | #147 |
| `get_call_graph` | Navigation | #137 |
| `find_event_subscribers` | Navigation | #139 |
| `get_overloads` | Navigation | #150 |
| `get_operators` | Navigation | #158 |
| `get_project_health` | Project health | #143 |
| `find_god_objects` | Project health | #145 |
| `generate_test_skeleton` | Generation | #154 |

---

## Deferred from shipped features

Items considered during design of shipped features and consciously punted on. Re-promote to the main backlog above if a use case emerges.

### From `get_operators` (shipped 2026-05-04)
- **Server-side filtering by kind** — agent filters `Kind` client-side; avoids a YAGNI parameter.
- **Inherited operators** — operators don't inherit in C#; `GetMembers` (declaration-only) is correct.
- **Indexers** — separate concern (`get_indexers` if ever needed).
- **Source-navigation links into metadata operators** — `peek_il` covers IL inspection.
- **Operator-resolution-against-arguments** — agent calls `find_callers` if needed.
- **Tighter test coverage on `OperatorInfo` fields** — current suite pins `Kind`/`Parameters`/`IsCheckedVariant`/`XmlDocSummary`/`ContainingType` but not `Signature`/`ReturnType`/`Accessibility`/`FilePath`/`Line`. Code reviewer suggested a single consolidated "fields populated" test on the documented `+(Money, Money)` operator. Cheap follow-up.
- **Conversion direction not pinned in tests** — current `Conversion_KindIsImplicitOrExplicit` confirms both kinds exist but doesn't verify `implicit` converts `Money → decimal` (vs reverse). A swapped `OperatorMap` mapping would still pass. Add `ReturnType`/`Parameters[0].Type` assertions to one of them.

### From `get_overloads` (shipped 2026-05-01, PR #150)
- **Source-navigation links into metadata overloads** — `peek_il` covers IL inspection separately.
- **Cross-type overloads from different containing types** — that's `find_implementations` territory.
- **Overload-resolution-against-arguments** — agent can call `find_callers` to see how each overload is invoked.
- **Operator overloads** — shipped as `get_operators` (2026-05-04).

### From `get_test_summary` (shipped 2026-05-01, PR #152)
- **Async-test flagging** (`IsAsync`) — could surface but isn't included now.
- **Skip-reason surface** for `[Fact(Skip = "…")]` / `[Ignore]` — agent can compute via `find_attribute_usages`.
- **`[MemberData]` / `[ClassData]` row tracking** — only inline rows are counted; data-source attributes don't expose row count without runtime evaluation.
- **Cross-project test→production coverage map** — that's `find_tests_for_symbol` territory in reverse.

### From `find_obsolete_usage` (shipped 2026-05-01, PR #147)
- **Reachability analysis per call site** — whether each call site is reachable from a test or public entry point. `analyze_change_impact` already covers this; agent can compose.
- **Auto-migration suggestions** — agent's call; tool stays diagnostic, not prescriptive.
- **`DiagnosticId` / `UrlFormat` attribute properties** — promote if agents start asking for them.
- **Inherited deprecation propagation** — Roslyn doesn't propagate `[Obsolete]` to overrides; can be inferred via `find_implementations`.

### From `get_project_health` (shipped 2026-04-30, PR #143)
- **Numeric "health score" or letter grade** — opinionated; agent computes client-side from counts.
- **Trend over time** — would require persistence layer.
- **Configurable dimension list** — YAGNI; agent calls underlying tool directly when it wants one dimension.

### From `find_god_objects` (shipped 2026-04-30, PR #145)
- **ML-based detection** — heuristic is enough.
- **Splitting / refactoring suggestions** — caller's judgment.
- **Reflection-coupling counted toward incoming-namespace tally** — separate concern.

### From `find_event_subscribers` (shipped 2026-04-29, PR #139)
- **Static-analysis leak detection** (subscribed-but-not-unsubscribed) — caller can compute client-side from result.
- **Reflection-based subscriptions** (`event.AddEventHandler(target, delegate)`).

### From `get_call_graph` (shipped 2026-04-29, PR #137)
- **Edge-level annotations** (call-site location per edge) — would expand JSON significantly.
- **Direction-aware path computation server-side** — agent can derive from the adjacency list.
- **Method-group expressions** (`Action a = obj.Method;`) — only direct invocations are followed.
- **Async state-machine awaits** as a separate edge kind — currently grouped with method calls.

### From `find_breaking_changes` (shipped 2026-04-29, PR #134)
- **Return-type changes** — `PublicApiEntry` schema doesn't capture them.
- **Sealed-ness changes** — same.
- **Nullable-annotation changes** — same.

### From `get_public_api_surface` (shipped 2026-04-29, PR #132)
- **Modifiers / return-type fields per entry** — defer until `find_breaking_changes` needs them.
- **Project / namespace filters** — whole-solution; add only on demand.
- **Inherited members shown per type** — declaration-only scope.
- **PublicAPI.txt format output** — JSON only; consumer can post-format.
- **Symbol XML doc comments** — not strictly part of the surface; defer.

### From `find_disposable_misuse` (shipped 2026-04-28, PR #129)
- **Method-argument disposables** (`DoSomething(new FileStream(...))`) — too ambiguous in v1.
- **try/finally + explicit `Dispose()`** — legacy pattern, would flag false positives on older codebases.
- **Wrapper-pattern ownership inference** (`StreamReader(stream)` owning inner) — only outer variable lifetime tracked.
- **`IDisposable` field not disposed in `Dispose()`** — CA1001 territory.
- **Aliasing tracking** — `var y = x;` doesn't propagate disposal back to `x`.
- **Conditional disposal flow analysis** — branch-sensitive disposal not tracked.
- **Lambdas / local functions / async methods nested-scope analysis** — follow-up.

### From `find_async_violations` (shipped 2026-04-28, PR #126)
- **`ConfigureAwait(false)` recommendations** — modern .NET often doesn't need it; would produce noise.
- **`Task.Run` on CPU-bound heuristics** — can't reliably distinguish CPU- from I/O-bound statically.
- **Custom-awaiter pattern detection** — vanishingly rare in user code.
- **Accessor / property / indexer body analysis** — only `MethodDeclarationSyntax` in v1.
- **Flow-sensitive "task assigned but never awaited"** — once a Task is in a variable, we trust the user.

### From `find_uncovered_symbols` (shipped 2026-04-28, PR #124)
- **Execution coverage from coverlet/dotCover XML** — different feature, runtime data.
- **Tunable `maxDepth` / `riskThreshold`** — hardcoded; add only if demand emerges.
- **Reflection-mediated coverage detection** — syntactic only.
- **"Tests touch this symbol but never assert anything"** — out of scope.

### From `find_tests_for_symbol` (shipped 2026-04-26, PR #116)
- **Coverage-data integration** (coverlet / dotCover parsing) — references, not runtime coverage.
- **Bidirectional view** (production code → tests, but not tests → production code). Use `analyze_method` on the test method.
- **Theory-row enumeration** — the method appears once.

### From `generate_test_skeleton` (shipped 2026-05-04, PR #154)
- **Property / indexer / operator stubs** — low value; agent can request manually.
- **Mock framework integration** (Moq, NSubstitute, FakeItEasy) — opinionated; agent picks.
- **Test data builders** (AutoFixture, Bogus) — same.
- **Cross-method dependency analysis** — keep skeleton focused on the SUT.
- **`SyntaxFactory`-based output** — string composition is cleaner for stub-shaped output.
- **Indirect `throw` detection** (via helper methods) — only direct `throw new T(...)` is followed.
- **Existing-test detection / merge** — agent handles dedupe.
- **Inherited-member skeletons** — agent composes via `get_overloads` / hierarchy tools.

### `generate_test_skeleton` known emitter limitations (fix as fast-follow)
- **Generic types** — `INamedTypeSymbol.Name` strips type parameters, so `Repository<TEntity>` emits `new Repository(...)` (invalid). Refuse with a clear error or close with `object` placeholder.
- **Nested types** — `targetType.Name` drops the outer-type qualifier, so `Outer.Inner` emits `new Inner(...)` (invalid). Use `MinimallyQualifiedFormat` for the SUT type expression.
- **Global-namespace types** — `ContainingNamespace.ToDisplayString()` returns empty for `IsGlobalNamespace`, producing `namespace .Tests;` and `using ;`. Guard with `IsGlobalNamespace`.
- **Throw-walk descends into lambdas / local functions** — a `throw` inside a `Where(...)` lambda is reported as if the outer method threw it directly. Filter by nearest enclosing method body.
- **Overload collisions** — two overloads of `Save(...)` both emit `Save_HappyPath`, producing duplicate method names. Suffix with arity or param-type initials.
- **Abstract types** — emitter still produces `new Abstract(...)` even though a TodoNote warns about it. Skip body emission or emit `null!` placeholder.
- **MSTest async-throw helper** — emitter always uses `Assert.ThrowsAsync<T>` (xUnit). MSTest needs `Assert.ThrowsExceptionAsync<T>`.
- **Primitive-param coverage** — `decimal`, `Int16`, `UInt16/32/64`, `SByte` not classified as primitives, so methods using them fall through to no-arg call branches.
- **Test coverage** — only xUnit emission tested deeply; NUnit `[TestCase]` and MSTest `[DataRow]` paths emit but aren't asserted.
