# Per-call `solution` argument Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every solution-bound tool accepts an optional `solution` argument that is resolved once per call, and `load_solution` gains `activate`. Parallel agents sharing one server no longer race on the active key.

**Architecture:** A pure `SolutionMatcher` gives one tiered matching rule. `MultiSolutionManager.Resolve(string? solution)` returns one `SolutionManager`, and each tool uses that manager for its whole call. `null` means the active solution.

**Tech Stack:** C# / .NET, ModelContextProtocol server attributes, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-28-per-call-solution-design.md`

## Global Constraints

- Non-breaking: every new parameter is optional, with defaults `solution = null` and `activate = true`. Commit type is `feat:`.
- Parameter: `string? solution = null`, placed before `CancellationToken` and after every existing optional parameter.
- Parameter description, verbatim: `"Optional loaded solution to run against, matched like set_active_solution. Defaults to the active solution. Pass it when several agents share this server."`
- Do not edit `SymbolResolver.cs`, any `Symbols/LogicalMemberGroups*` or `LogicalAmbiguity*` file, or any `*Logic.cs` file. Open PR #603 changes those.
- New logic must be unit-testable without a file watcher. Build managers from `SolutionManager.CreateEmpty()`. Tests that load `TestSolution` are verified in CI, not locally.
- Run one `dotnet test` at a time, against one project, with `--filter` where possible.
- Commit bodies must not contain nested parentheses. No Claude trailers.

## Review Focus

1. A full path whose file name is also a substring of another key (`/a/A.sln` alongside `/b/DATA.sln`) resolves to the exact path, not to `AmbiguousMatch`. Covered by a Task 1 test.
2. A path with different casing or a trailing `./` segment still hits tier 1, via `Path.GetFullPath` and a case-insensitive comparison. Covered by a Task 1 test.
3. Whitespace or an empty `solution` (`""`) means "not passed", not "match everything by substring". Today `Contains("")` matches every key. Covered by Task 2 `Resolve_EmptyString_ReturnsActive`.
4. `load_solution(activate:false)` on a path that is already loaded without a filter is a no-op. It does not switch the active solution. Covered by a Task 4 unit test of `NextActiveKey`.
5. `start_background_task` with `solution` rebuilds that solution even if the active key changes before the task runs. Covered by the Task 3 test `StartBackgroundTask_UnknownSolution_ThrowsBeforeQueueing`, which proves the solution is resolved at queue time.

---

### Task 1: SolutionMatcher

**Files:**
- Create: `src/RoslynCodeLens/SolutionMatcher.cs`
- Test: `tests/RoslynCodeLens.Tests/SolutionMatcherTests.cs`

**Interfaces:**
- Produces: `internal static class SolutionMatcher { public static string Match(IReadOnlyCollection<string> keys, string name); }`. It returns the matched key or throws `McpToolException`.

The tiers stop at the first tier with any match:
1. `string.Equals(Path.GetFullPath(name), key, OrdinalIgnoreCase)`. Wrap `GetFullPath` in a try, because invalid path chars mean the name simply never matches tier 1.
2. `string.Equals(Path.GetFileName(key), name, OrdinalIgnoreCase)`.
3. `key.Contains(name, OrdinalIgnoreCase)`.

One match in a tier returns it. More than one throws `AmbiguousMatch`. No match in any tier throws `ProjectNotFound`. The messages and details objects are copied exactly from today's `SetActiveSolution`: `{ name, available }` and `{ name, matches }`.

- [ ] **Step 1: Write the failing tests.** Use keys `C:/x/A.sln`, `C:/y/DATA.sln` and `C:/z/Web.slnx`, built with `Path.GetFullPath` so they work on Linux:
  - `Match_FullPath_WinsOverSubstring`: `A.sln`'s full path returns the A key.
  - `Match_FullPathDifferentCase_Matches`: an upper-cased full path returns the same key.
  - `Match_FileName_WinsOverSubstring`: `"A.sln"` returns the A key, and does not throw `AmbiguousMatch` against `DATA.sln`.
  - `Match_Substring_UniqueMatch`: `"Web"` returns the Web key.
  - `Match_Substring_Ambiguous_Throws`: `".sln"` throws `ToolErrorCode.AmbiguousMatch`.
  - `Match_NoMatch_Throws`: `"Nope"` throws `ToolErrorCode.ProjectNotFound`.
- [ ] **Step 2: Run `dotnet test tests/RoslynCodeLens.Tests --filter SolutionMatcherTests`.** Expected: compile failure, because `SolutionMatcher` does not exist yet.
- [ ] **Step 3: Implement `SolutionMatcher.Match`.**
- [ ] **Step 4: Run the same command.** Expected: 6 pass.
- [ ] **Step 5: Wire `SetActiveSolution` and `UnloadAndDispose` to `SolutionMatcher.Match`.** Pass `_managers.Keys.ToList()` and the snapshot respectively. Run `--filter "SolutionMatcherTests|MultiSolutionManagerTests.SetActiveSolution_UnknownName_Throws|MultiSolutionManagerTests.UnloadSolution_UnknownName_Throws"`. Expected: pass.
- [ ] **Step 6: Commit** `feat: match solutions by exact path, then file name, then substring`.

### Task 2: `MultiSolutionManager.Resolve`

**Files:**
- Modify: `src/RoslynCodeLens/MultiSolutionManager.cs`
- Test: `tests/RoslynCodeLens.Tests/MultiSolutionManagerResolveTests.cs`

**Interfaces:**
- Consumes: `SolutionMatcher.Match`.
- Produces:
  - `public SolutionManager Resolve(string? solution)`. `null`, empty or whitespace returns `Active`, read once. Anything else returns `_managers[SolutionMatcher.Match(_managers.Keys.ToList(), solution)]`.
  - `internal static MultiSolutionManager CreateForTesting(IReadOnlyDictionary<string, SolutionManager> managers, string? activeKey)`.
  - `public IReadOnlyList<SkippedProjectInfo> GetSkippedProjects(string? solution)` and `public IReadOnlyList<string> GetLoadDiagnostics(string? solution)`. These have the same bodies as the `GetActive*` methods, but use `Resolve(solution)`. The `GetActive*` methods become `=> Get…(null)`.
- Change: `public` `SolutionManager` members stay as they are. The existing instance methods `EnsureLoaded` and the others keep delegating to `Active`.

- [ ] **Step 1: Write the failing tests.** Use `CreateForTesting` with two `SolutionManager.CreateEmpty()` instances, keyed by `Path.GetFullPath("/tmp/a/A.sln")` and `Path.GetFullPath("/tmp/b/B.sln")`, with A active:
  - `Resolve_Null_ReturnsActive` and `Resolve_EmptyString_ReturnsActive`: `Assert.Same` the A manager.
  - `Resolve_Name_ReturnsNamedWithoutChangingActive`: `Resolve("B")` returns the B manager, and `ListSolutions().Single(s => s.IsActive).Path` is still A.
  - `Resolve_Unknown_ThrowsProjectNotFound`.
  - `Resolve_NoActive_Null_ThrowsInvalidArgument`: `CreateForTesting` with a `null` activeKey.
- [ ] **Step 2: Run `--filter MultiSolutionManagerResolveTests`.** Expected: compile failure.
- [ ] **Step 3: Implement `Resolve`, `CreateForTesting`, `GetSkippedProjects` and `GetLoadDiagnostics`.**
- [ ] **Step 4: Run the same filter.** Expected: 5 pass.
- [ ] **Step 5: Commit** `feat: resolve a named solution per call without touching the active key`.

### Task 3: `solution` on the 58 solution-bound tools

**Files:**
- Modify: every `src/RoslynCodeLens/Tools/*Tool.cs` whose `Execute` takes `MultiSolutionManager`, except `SetActiveSolutionTool`, `UnloadSolutionTool`, `LoadSolutionTool` and `ListSolutionsTool`. That is 58 tools.
- Test: `tests/RoslynCodeLens.Tests/Tools/SolutionArgumentCoverageTests.cs` and `tests/RoslynCodeLens.Tests/Tools/StartBackgroundTaskToolTests.cs`.

**Interfaces:**
- Consumes: `MultiSolutionManager.Resolve(string?)`.
- Pattern for each tool: add the parameter, then replace every `manager.X` in the body with `sm.X`, after `var sm = manager.Resolve(solution);`. Resolve once, at the point where the first `manager.` call is today. Keep any argument validation that runs before `EnsureLoaded`, such as `ValidateKinds`, where it is. Write tools pass `sm.CommitDocumentTextsAsync`.
- `RebuildSolutionTool`: `Resolve`, then `sm.EnsureLoaded()`, then `sm.ForceReloadAsync()`.
- `StartBackgroundTaskTool`: `var sm = manager.Resolve(solution);` before `store.Start`. The closure captures `sm`, not `manager`.

- [ ] **Step 1: Write the failing coverage test** `EverySolutionBoundTool_DeclaresOptionalSolution`. Use reflection over `typeof(MultiSolutionManager).Assembly`: every method with `[McpServerTool]` that has a `MultiSolutionManager` parameter, and whose tool `Name` is not in `{set_active_solution, unload_solution, load_solution, list_solutions}`, must have a parameter named `solution` of type `string`, with `HasDefaultValue` and `DefaultValue == null`. Assert that the set of tools checked has exactly 58 entries, and that the list of missing tools is empty. Put the missing tool names in the message.
- [ ] **Step 2: Write the failing test** `StartBackgroundTask_UnknownSolution_ThrowsBeforeQueueing` in `StartBackgroundTaskToolTests`. Build a manager with `CreateForTesting`, with A active. `Execute(manager, store, "rebuild_solution", solution: "Nope")` throws `ProjectNotFound` synchronously, and `store` has no tasks. That proves the solution is resolved at queue time, not when the task runs.
- [ ] **Step 3: Run `--filter "SolutionArgumentCoverageTests|StartBackgroundTaskToolTests"`.** Expected: the coverage test fails and lists 58 tools, and the background test fails to compile.
- [ ] **Step 4: Apply the pattern to all 58 tools.**
- [ ] **Step 5: Run `dotnet build -warnaserror`, then the same filter.** Expected: the build is clean and both tests pass.
- [ ] **Step 6: Commit** `feat: accept an optional solution argument on every solution-bound tool`. The body names #606, says the call is resolved once so write tools commit to the solution they read, and says background rebuilds resolve at queue time.

### Task 4: `load_solution` `activate`

**Files:**
- Modify: `src/RoslynCodeLens/MultiSolutionManager.cs` (`LoadSolutionAsync`)
- Modify: `src/RoslynCodeLens/Tools/LoadSolutionTool.cs`
- Test: `tests/RoslynCodeLens.Tests/MultiSolutionManagerResolveTests.cs` and `tests/RoslynCodeLens.Tests/Tools/LoadSolutionToolTests.cs`

**Interfaces:**
- Produces: `public Task<string> LoadSolutionAsync(string solutionPath, ProjectFilter? filter = null, bool activate = true)`, and `internal static string? NextActiveKey(string? current, string loaded, bool activate)`, which returns `activate || current is null ? loaded : current`. Every `_activeKey = normalised` in `LoadSolutionAsync` becomes `_activeKey = NextActiveKey(_activeKey, normalised, activate)`. That covers the already-loaded fast path and the final assignment.
- Tool: add `bool activate = true` after `background`, with the description `"If false, load without changing the active solution for other callers; pass the returned path as solution on later calls. Default true. When no solution is active, it becomes active anyway."` Both paths read their results through `manager.GetSkippedProjects(normalised)`, `manager.GetLoadDiagnostics(normalised)` and `manager.Resolve(normalised).GetLoadedSolution()`, instead of the `GetActive*` members. Append to the tool description: `"Pass activate: false to load without switching the active solution."`

- [ ] **Step 1: Write the failing tests** in `MultiSolutionManagerResolveTests`:
  - `NextActiveKey_ActivateTrue_ReturnsLoaded`
  - `NextActiveKey_ActivateFalse_KeepsCurrent`
  - `NextActiveKey_ActivateFalseNoCurrent_ReturnsLoaded`
- [ ] **Step 2: Write the CI-verified test** `LoadSolution_ActivateFalse_KeepsActive` in `LoadSolutionToolTests`. Load `TestSolution`, then load `TestSolutionAlt` with `activate:false`. The active solution is still `TestSolution`, and `Resolve("TestSolutionAlt")` works.
- [ ] **Step 3: Run `--filter "MultiSolutionManagerResolveTests"`.** Expected: compile failure.
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run `--filter MultiSolutionManagerResolveTests`.** Expected: pass. The `LoadSolutionToolTests` test may hit "Solution warmup failed" locally, because of the inotify limit. Leave it to CI.
- [ ] **Step 6: Commit** `feat: let load_solution load without switching the active solution`.

### Task 5: Docs

**Files:**
- Modify: `README.md` (around line 77-78, 142, 266 and 292)
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` (the error table around line 45-47, the solution-tools list around line 310, and the parameter conventions section)
- Check: `docs/site` pages that duplicate the README tool list or the error codes. Run `grep -rn "set_active_solution" docs/site --include=*.md*`, and update the same sentences there.

Content:
- **README, after line 266:** a "Parallel agents" paragraph. Every tool that works on a solution takes an optional `solution`, matched by full path, then file name, then substring. With `solution`, a call never depends on the shared active solution, and `load_solution(activate: false)` loads without switching it.
- **README, line 292:** the parenthetical becomes `(set_active_solution affects everyone; pass solution per call to avoid that)`.
- **README, line 142:** the `AmbiguousMatch` line, and the `ProjectNotFound` line if there is one, also cover "any tool's `solution` argument".
- **SKILL.md:** the error table's "Tools" column for those two rows adds "any tool's `solution` argument". Add one bullet: "Several agents on one server: pass `solution` on every call instead of `set_active_solution`."

- [ ] **Step 1: Make the edits.**
- [ ] **Step 2: Run `dotnet test tests/RoslynCodeLens.Tests --filter "ToolDescriptionMdxSafetyTests|ToolSelectionTests"`.** Expected: pass, which confirms the new descriptions are MDX-safe.
- [ ] **Step 3: Commit** `docs: document the per-call solution argument and load_solution activate`.

### Task 6: Verify and open the PR

- [ ] **Step 1: Run `dotnet build -warnaserror` and the unit filters from Tasks 1-4, one at a time.**
- [ ] **Step 2: Use `pre-push-review`, then push `feat/606-per-call-solution`, then open the PR from the repo's template.** Tick only what is true, and note that the watcher-dependent tests are verified in CI.
