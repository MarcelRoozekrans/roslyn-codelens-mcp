# Per-call `solution` argument (#606)

## Why

Several agents can share one server process (one HTTP server, or one client that multiplexes agents),
each on a different loaded solution. `set_active_solution` and `load_solution` change a single active
key for every caller, so agent A's next `find_callers` can answer from agent B's solution. A per-call
argument makes each call self-contained.

## Goals

- An optional `solution` argument on every tool that works against a loaded solution, resolved once
  per call.
- `load_solution` can load without changing the active solution for everyone else.
- Without the new arguments, behaviour is unchanged. Non-breaking, ships as `feat:`.

Non-goals: per-connection or per-session active solutions; guarding a call against its solution
being unloaded mid-call. That window exists today and stays as it is.

Anti-goal: a named solution that is not loaded must never fall back to the active one. That silent
fallback is the bug being fixed.

## Approaches

1. **`Resolve(string? solution)` returns one `SolutionManager` per call (chosen).** Each tool resolves
   once and uses that manager for the whole call: load, analysis context, commit. This also closes the
   race that exists today inside a single call. `EnsureLoaded`, `GetAnalysisContext` and
   `CommitDocumentTextsAsync` each re-read `Active`, so a switch in between makes a write tool commit
   edits computed on A into B's snapshot.
2. **Overloads like `GetAnalysisContext(string? solution)`.** A smaller diff per tool, but it still
   resolves two or three times per call, so the races inside a call stay.
3. **An ambient `AsyncLocal` solution set by the tool wrapper.** No per-tool body changes, but the
   parameter would still have to sit on every signature for the schema, unused, and the flow becomes
   implicit.

## Design

### Scope

58 of the 67 tools are bound to a solution, and all 58 get `solution`. That covers the read tools, the
write tools `rename_symbol`, `change_signature` and `apply_code_action`, plus `rebuild_solution` and
`start_background_task`.

These do not get it:
- No solution context: `get_task_status`, `list_running_tasks`, `list_trusted_paths`,
  `trust_solution`, `revoke_trust`, `list_solutions`.
- Already take a solution name or path: `set_active_solution`, `unload_solution`, `load_solution`.

Parameter: `string? solution = null`, placed before any `CancellationToken`. Description: "Optional
loaded solution to run against, matched like set_active_solution. Defaults to the active solution.
Pass it when several agents share this server."

### MultiSolutionManager

- `SolutionManager Resolve(string? solution)`: null means the active solution, read once. Otherwise
  the shared matcher below is used.
- Shared matcher, used by `Resolve`, `SetActiveSolution` and `UnloadSolution`, in this precedence:
  1. exact full-path match (after `Path.GetFullPath`, case-insensitive)
  2. exact file-name match
  3. the substring match used today

  It stops at the first tier with any match. If that tier has exactly one, it is used. More than one
  gives `AmbiguousMatch` with `details.matches`. No match in any tier gives `ProjectNotFound` with
  `details.available`. Every name that matches uniquely today resolves the same way. Only some names
  that are ambiguous today now resolve, for example `A.sln` alongside `DATA.sln`.
- The existing `Active`-based members stay and delegate to `Resolve(null)`.

### Tools

Each tool body becomes `var sm = manager.Resolve(solution); sm.EnsureLoaded(); var ctx = sm.GetAnalysisContext();`
or the matching getter. The write tools pass `sm.CommitDocumentTextsAsync`. `start_background_task`
and `rebuild_solution` resolve when the task is queued, not when it runs, so a background rebuild
always rebuilds the solution it was asked for.

### Warm-up

A call that names a solution blocks on that solution's warm-up, the same way a call on the active
solution does today. A failed warm-up gives the existing "Solution warmup failed" error.

### load_solution `activate`

`activate: bool = true`. With `false`, the solution is loaded, or re-loaded with a filter, without
touching the active key. One exception: when there is no active solution, it becomes active anyway,
since no caller can depend on an empty key. The result reads its skipped and degraded projects from
the loaded solution by key instead of from `Active`, which also fixes the same race in today's
background path. The background path honours `activate` too.

### Docs

- README: a "Parallel agents" paragraph in the multi-solution section, the HTTP note that
  "`set_active_solution` affects everyone" points to `solution`, the `AmbiguousMatch` and
  `ProjectNotFound` lines list every tool that takes `solution`, and the `load_solution` line mentions
  `activate`.
- SKILL.md: one line in the parameters and conventions area, the error table rows for
  `AmbiguousMatch` and `ProjectNotFound` extended, and the `load_solution` line mentions `activate`.

### Tests

These are unit tests on `MultiSolutionManager` that need no file watcher:
- the matcher tiers
- the error codes
- `Resolve(null)` equals the active solution
- `activate: false` leaves the key alone, except when it is empty

Plus one test that asserts by reflection that every solution-bound tool declares `solution`. That
test also catches a future tool that forgets it. Tool-level tests on the test solution run in CI.

## Constraints

- Avoid conflicts with open PR #603. It changes only `*Logic.cs` files and `SymbolResolver`, and the
  argument goes in the `*Tool.cs` files.
- Keep the new logic unit-testable on Linux without a file watcher.

## Left to the builder

The exact wording of the descriptions and docs, and the test names.
