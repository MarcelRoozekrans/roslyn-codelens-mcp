# Backlog

Ideas for future tools, grouped by theme. Not committed work — captured here so they're not lost. Each entry is a starting point for a real design discussion.

Last refreshed: 2026-07-19.

---

## 1. Async & concurrency tools

Common .NET pain points that aren't covered by analyzers everyone has on by default.

- **`find_thread_safety_issues`** — lock usage patterns, shared mutable state in static fields, captured locals in tasks. *Note: deep heuristic territory; design carefully or punt.*

## 2. Navigation niceties

Small, focused queries that aren't currently expressible in one call.

- **`find_duplicated_code`** — heuristic detection of repeated statement blocks across files. *Note: existing tools (JSCPD, SonarQube) cover this well; only worth doing if a Roslyn-semantic angle is identified. SharpLensMcp ships this as `find_similar_code` using token-shingle fingerprints — a viable middle ground; would slot into `get_project_health` and the refactor-analysis skill.*

## 3. Generation & scaffolding (write-side)

Companions to `apply_code_action`, but for shapes that Roslyn doesn't ship out of the box.

- **`generate_dto_from_class`** — given a domain class, emit a DTO + AutoMapper-style mapping (or manual `ToDto`/`FromDto` extension methods). *Note: opinionated — picking a mapping style is the hard part.*
- **`generate_builder`** — fluent builder for a class, including required-property tracking.

## 4. Startup & loading performance

Big-solution scenarios (400+ projects) where the structural open dominates wall-clock and blocks the client agent. The project-filter feature (issue [#232](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/232)) was the first step; the items below were its deferred companions.

- ✅ **Parallelise the per-project loader** — *shipped.* `SolutionLoader.OpenPerProjectAsync` now loads projects across a bounded pool of isolated `MSBuildWorkspace` workers (each its own out-of-process `BuildHost`), with global path-dedup to curb redundant transitive loads, then re-stitches the results into one workspace. Confirmed via experiment that concurrent `OpenProjectAsync` on a *shared* workspace corrupts the solution, so isolation + re-stitch is required. Degree via `ROSLYN_CODELENS_LOAD_PARALLELISM` (default `min(CPU, 8)`). Design: [docs/plans/2026-06-18-parallel-project-loader-design.md](plans/2026-06-18-parallel-project-loader-design.md).
- ✅ **Async `load_solution` with a load handle** — *shipped.* `load_solution` gained a `background: true` flag: it runs the load on the existing `BackgroundTaskStore` and returns a `taskId` immediately; the agent polls `get_task_status`. Turned out far smaller than feared — no `SolutionManager`/`EnsureLoaded` changes, because the new solution only becomes active once the background load finishes, so other tools never block on it. Reuses the background-task infra (which postdates this note) rather than a bespoke `get_load_status`. Design: [docs/plans/2026-06-18-async-load-solution-design.md](plans/2026-06-18-async-load-solution-design.md).

## 5. Gaps identified from SharpLensMcp comparison (2026-07-19)

Comparison against [sharplens-mcp](https://github.com/pzalutski-pixel/sharplens-mcp/) (91 tools). Most of its catalog overlaps ours or is covered by `apply_code_action`; the items below are genuine gaps. Low-value items (`semantic_query`, `check_type_compatibility`, `find_interceptors`, `remove_unused_code`, `organize_usings`, `format_document_batch`) were considered and skipped — niche, or covered by `dotnet format` and existing find-tools.

### High value

- ✅ **`rename_symbol`** — *shipped* (PR #298; review-findings hardening in [docs/plans/2026-07-19-rename-review-fixes.md](plans/2026-07-19-rename-review-fixes.md)). Design: [docs/plans/2026-07-19-rename-symbol-design.md](plans/2026-07-19-rename-symbol-design.md).
- ✅ **`resolve_stack_trace`** — *shipped* (PR #301). Map a pasted runtime stack trace to file/line/symbol, undoing name mangling (async state machines, lambdas, local functions, generics). Design: [docs/plans/2026-07-19-resolve-stack-trace-design.md](plans/2026-07-19-resolve-stack-trace-design.md).
- ✅ **`get_method_source`** — *shipped* (PR #305). Returns members' full declaration source by name; one tool with array input covers SharpLens's scalar + batch pair. Design: [docs/plans/2026-07-19-get-method-source-design.md](plans/2026-07-19-get-method-source-design.md).
- ✅ **Reference-kind classification on `find_references`** — *shipped* (PR #307). Each reference is tagged (`read`/`write`/`readwrite`/`invocation`/`method_group`/`object_creation`/`cast`/`type_check`/`typeof`/`base_type`/`type_constraint`/`type_argument`/`declaration`/`attribute`/`nameof`/`xml_doc`), reported per occurrence with a `column`, filterable server-side via `kinds`, and summarised by `byKind`. Subsumes the would-be `find_pattern_usages` (`type_check`). Enhancement, so no Recently-shipped row. Design: [docs/plans/2026-07-20-reference-kinds-design.md](plans/2026-07-20-reference-kinds-design.md).
- ✅ **Exception-flow trio: `get_exception_flow`, `find_throw_sites`, `find_catch_blocks`** — *shipped* (PR #309). Depth-bounded escape analysis with propagation paths, plus solution-wide throw/catch site queries including swallow detection. Design: [docs/plans/2026-07-20-exception-flow-design.md](plans/2026-07-20-exception-flow-design.md). **This completes every high-value item from the SharpLens comparison.**

### Medium value

- ✅ **`change_signature`** — *shipped* (PR #313). Add/remove/reorder parameters with all call sites updated, via a reflection bridge over Roslyn's internal change-signature engine (no public API exists, unlike `Renamer`). Design: [docs/plans/2026-07-20-change-signature-design.md](plans/2026-07-20-change-signature-design.md).
- ✅ **`get_extension_methods`** — *shipped* (PR #319). Applicability via Roslyn's own `ReduceExtensionMethod`/`ReduceExtensionMember`, across solution source and referenced metadata, including C# 14 extension blocks and their properties. Design: [docs/plans/2026-07-21-get-extension-methods-design.md](plans/2026-07-21-get-extension-methods-design.md).
- ✅ **`get_instantiation_options`** — *shipped* (PR #327). Constructors (with the record copy-ctor filtered and implicit struct/class ctors kept), solution-wide static factories including ones on a separate factory type, DI registrations, and `required` members. Optional `fromProject` computes real accessibility via `IsSymbolAccessibleWithin`, honouring `InternalsVisibleTo`. Also fixed `generate_test_skeleton`, which emitted an uncompilable `new Foo()` for private-constructor types. Design: [docs/plans/2026-07-21-get-instantiation-options-design.md](plans/2026-07-21-get-instantiation-options-design.md).
- ✅ **Cognitive complexity + nesting depth in `get_complexity_metrics`** — *shipped*. `cognitive` (SonarSource rules: nesting penalty, one point per `switch` rather than per case, `else`/`else if` flat, boolean sequences once per sequence, recursion) and `maxNesting` alongside the existing cyclomatic `complexity`, selected by a new `metric` parameter. Also corrected three cyclomatic defects (`else` counted as a decision, `default:` counted as a case, switch *expressions* invisible) and widened member discovery beyond methods to constructors, properties, indexers and operators. Design: [docs/plans/2026-07-22-cognitive-complexity-design.md](plans/2026-07-22-cognitive-complexity-design.md). **This closes the SharpLens gap analysis — no items remain in §5.**
- ✅ **`check_architecture`** — *shipped* (PR #314). Enforces user-supplied `forbid`/`allowOnly` rules over the semantic type graph (not `using` directives), grouped per violated boundary. Design: [docs/plans/2026-07-20-check-architecture-design.md](plans/2026-07-20-check-architecture-design.md).
- **`find_similar_code`** — folded into the existing `find_duplicated_code` entry in §2 above.

**Section status: complete.** Every high-value and medium-value gap identified in the SharpLens comparison has now shipped. Nothing here is outstanding; new work should start a new section rather than extend this one.

---

## In flight

Active branches with no merged PR yet.

_(none currently)_

---

## Recently shipped

Items previously in this backlog, now merged. Listed for orientation; do not re-design without confirming.

| Tool | Theme | PR |
|---|---|---|
| `get_instantiation_options` | Navigation | #327 |
| `get_extension_methods` | Navigation | #319 |
| `check_architecture` | Code quality | #314 |
| `change_signature` | Refactoring | #313 |
| `get_exception_flow` | Analysis | #309 |
| `find_throw_sites` | Analysis | #309 |
| `find_catch_blocks` | Analysis | #309 |
| `get_method_source` | Analysis | #305 |
| `resolve_stack_trace` | Navigation | #301 |
| `rename_symbol` | Refactoring | #298 |
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

### From cognitive complexity in `get_complexity_metrics` (shipped 2026-07-22)
- **The recursion rule is an identifier match, not a semantic one, so it can false-positive.** Cognitive complexity adds +1 when a member calls itself. `CognitiveComplexityCalculator` is a pure syntax primitive with no semantic model, so it decides "calls itself" by comparing the invoked identifier to the member's own name. A method `Foo.Process` that calls an unrelated `Bar.Process` — or a same-named overload, or a local variable's delegate — scores the recursion point it has not earned. The overcount is 1 on a metric already advisory, and threading a `SemanticModel` through would make the calculator no longer a pure syntax primitive, so it was accepted deliberately rather than overlooked. Fix it only if a real solution shows a misleading ranking.
- ✅ **`GetProjectHealthToolTests.Counts_ComplexityMatchesUnderlyingTool` was vacuous** — *fixed.* It asserted the composite's `ComplexityHotspots` equalled the underlying tool's count at `threshold: 10`, and both were **0** in every project because the fixture's most complex member scored cyclomatic 5 — so the whole complexity dimension of `get_project_health` was untested and the test would have passed if that dimension returned nothing at all. `TestLib/ComplexitySamples` now provides `Classify` (cyclomatic 13) and `DeeplyNested` (cognitive 15 vs cyclomatic 6), with an `Assert.NotEmpty` guard so the test fails loudly if the fixture ever stops reaching the threshold. **The reason this was deferred did not hold:** the feared movement in absolute counts across the unused/uncovered/public-API suites never happened, because those suites assert relationally. Full suite green at 1131 with no downstream assertion changed.
- **A property's `complexity` and `cognitive` can come from different accessors.** A property is one row scored by its worst accessor, and the max is taken **per metric independently**: a getter with cyclomatic 6 / cognitive 2 alongside a setter with cyclomatic 3 / cognitive 5 reports `complexity: 6, cognitive: 5`, a pair no single accessor has. Reporting a whole accessor's numbers instead would mean choosing which metric decides "worst", which is worse for the caller who filtered on the other one. Only visible on properties with two non-trivial accessors, which are rare; revisit if a caller is misled.

### From `get_extension_methods` (shipped 2026-07-21, PR #319)
- **Scan each referenced assembly once, not once per referencing compilation.** A metadata receiver (`string`, `int`, any BCL type) is resolved by every compilation, so candidate gathering runs N times over largely the same framework closure — N× the measured 59 ms warm / 815 ms cold. The results are deduplicated but the *work* is not. Only each compilation's own source types genuinely differ; the referenced assemblies produce identical answers every time. Correctness came first (the single-compilation shortcut dropped the solution's own extensions on BCL types), but this is the obvious next step for large solutions.

### From `get_instantiation_options` (shipped 2026-07-21, PR #327)
- **Multi-targeted projects double-count DI registrations.** `Foo(net8.0)` and `Foo(net9.0)` are distinct `Project.Name`s, so the project-scoped dedupe treats them as two projects and reports each registration twice. Pre-existing — the hand-rolled loop did the same — and *not* fixed by the `SolutionScanner` migration, because a project-name scope is exactly what linked files require. A correct fix needs a project identity that collapses target frameworks without merging genuinely distinct projects; TFM-suffix stripping is a guess, not a rule, so it was deliberately not attempted.
- **Generic types don't match DI registrations.** `get_instantiation_options` passes the type to the scanner as `Demo.Foo<T>`, which never string-matches a registration of `Demo.Foo<int>`. Narrow, and untested today.
- **`ActivatorUtilities.CreateInstance<Foo>(sp)` factories yield no implementation name.** The factory-lambda reader follows `sp => new X()` only; anything else degrades to `"(factory)"` rather than guessing. That degradation is tested and correct, but this particular form is common enough to be worth reading properly.
- **Two DI tests discriminate on line number** (`Finds_two_type_generic_registration`, `Finds_factory_lambda_registration`). Both registrations are `(Demo.IFoo, Demo.Foo, Singleton)` and differ only by position, so there is no other key — editing the shared `Startup` test source will silently break them.
- **`get_instantiation_options` runs a full solution scan per call** to find factories, even for interfaces and static classes where the answer is usually empty. Same shape as the `get_extension_methods` note above.

### From `check_architecture` (shipped 2026-07-20, PR #314)
- ✅ **Extract the shared solution-wide semantic scan walker** — *shipped* (PR #316). `Analysis/SolutionScanner.cs` owns compilation enumeration, generated-tree skipping, robust dedupe (tree identity with a content-hash fallback, plus an optional scope discriminator) and cancellation; callers keep their own node loops and receive a **lazy** semantic-model accessor. Migrating `find_throw_sites` and `find_catch_blocks` onto it propagated two fixes they had been missing: pathless trees no longer double-count, and project attribution for a linked or multi-targeted file is deterministic rather than decided by `ConcurrentDictionary` enumeration order. Design: [docs/plans/2026-07-20-solution-scanner-design.md](plans/2026-07-20-solution-scanner-design.md).
- **Migrate the remaining scan tools onto `SolutionScanner`** — `find_async_violations`, `find_disposable_misuse`, `find_obsolete_usage`, `find_event_subscribers` and any other tool that still hand-rolls the compilation/tree loop. Each one left un-migrated is a place the two fixes above have not landed. Mechanical: the loop becomes `foreach (var scan in SolutionScanner.EnumerateTrees(...))` with the existing body, and each tool's suite is the regression net. Kept out of PR #316 to hold its blast radius to the three tools with proven drift.

### From `get_method_source` (shipped 2026-07-19, PR #305)
- **`KindOf` consolidation** — roughly five tools carry their own local symbol-kind mapper (`method`/`property`/`field`/...); extract one shared helper so kind strings can't drift between tools.
- **Constructor resolution in `SymbolResolver`** — the `Type.Type` / `.ctor` request handling lives in `GetMethodSourceLogic`; move it into `SymbolResolver` so every tool resolves constructors the same way.
- **Partial-qualification suffix matching** — resolver support for `Outer.Inner.Member`-style requests (today: simple name or fully qualified only; nested types need full qualification).
- **Collapse the property/field/event Facts into a Theory** — `GetMethodSourceLogicTests` has one near-identical Fact per member kind; a `[Theory]` would shrink the suite without losing coverage.

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
