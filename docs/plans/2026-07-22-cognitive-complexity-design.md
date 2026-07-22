# Cognitive complexity + nesting depth — design

**Goal:** report cognitive complexity and maximum nesting depth alongside cyclomatic in `get_complexity_metrics`, and correct three defects the existing cyclomatic calculation has.

Backlog item: `docs/BACKLOG.md` §5, medium tier — the last item from the SharpLens gap analysis.

## Why this is more than an added field

Probing the current calculator against real Roslyn (scratchpad probe, reproduced in tests) found `ComplexityCalculator.Calculate` already reports wrong numbers:

| Case | Reported today | Correct | Cause |
|---|---|---|---|
| `if / else if / else if / else` | **7** | 4 | counts both `IfStatement` and `ElseClause`, so an `else if` is one decision counted twice — and a bare `else` is not a decision at all |
| `a switch { 1 => …, 2 => …, _ => … }` | **1** | 4 | only `SwitchSection` is counted; switch *expressions* are invisible |
| constructors, properties, accessors | not reported | — | the scan visits only `MethodDeclarationSyntax` |

The tool exists to rank refactoring priority, so it currently over-states classic if/else chains and under-states modern switch-expression code — a bias in the exact judgement it informs. Approved to fix rather than preserve.

## Output shape

`ComplexityMetric` gains two fields. `Complexity` keeps its name and its meaning (cyclomatic), so `get_project_health` and `find_uncovered_symbols` stay shape-compatible:

```
{ methodName, typeName, complexity: 12, cognitive: 19, maxNesting: 4, file, line, project }
```

## Parameters

`metric: "cyclomatic" | "cognitive"`, default `"cyclomatic"`. Both values are always reported; `metric` selects which one `threshold` filters on and which the worst-first sort uses. The default keeps every existing caller — `get_project_health` included — behaving exactly as today.

## Cyclomatic rules (corrected)

- +1 per `IfStatement`. **`ElseClause` is not counted**: `else` introduces no decision, and `else if` is already counted by its own `IfStatement`.
- +1 per switch label **except `default`**; +1 per switch-expression arm **except the discard `_`**.
- +1 per loop (`for`, `foreach`, `while`, `do`), `catch`, conditional expression.
- +1 per `&&`, `||`, `??` token (unchanged).

## Cognitive rules (SonarSource)

- **+1 plus current nesting** for: `if`, ternary, `switch`, loops, `catch`.
- **+1 with no nesting penalty** for: `else`, `else if`, `goto`, labelled `break`/`continue`.
- **+1 per boolean operator SEQUENCE**, not per operator: `a && b && c` is +1, `a && b || c` is +2. Detected by counting logical binary nodes whose parent is not a binary node of the same kind — verified against Roslyn's tree shape.
- **Nesting level increases** for those structures and also for lambdas and local functions; lambdas and local functions do **not** themselves score +1.
- +1 for direct recursion.

`maxNesting` is the deepest control-structure nesting reached, which falls out of the same walk.

## Attribution

Lambdas and local functions count toward their **enclosing member** rather than becoming separate rows. The question is "which member do I refactor", and a row for a lambda with no name is not an answer.

Members analysed: methods, constructors, properties and their accessors, indexers, operators, and destructors.

## Scanner migration

`GetComplexityMetricsLogic` hand-rolls the compilation/tree loop, so it counts generated code and double-counts multi-targeted projects. It moves to `SolutionScanner`, consistent with fixing wrong numbers.

This is the same class of change that silently broke `find_obsolete_usage` and `find_event_subscribers` (see `2026-07-21-scan-migration-design.md`), but lower risk here: results are keyed by file and line, with no cross-compilation symbol identity involved — which is what failed there. Pinned by a two-projects-sharing-a-file-path test and repeated runs, not by argument.

Generated code is **excluded** (the default), unlike the DI scan: a complexity finding is something a human is expected to go and fix, and nobody refactors a generated file.

## Testing

- Every corrected case asserts the **specific** number, with the old wrong value named in a comment, so a regression is unmistakable rather than a silent drift.
- Cognitive scores are checked against worked examples from the Sonar specification, not against arithmetic done here.
- The nesting penalty is pinned by a case where cyclomatic and cognitive **disagree** (`foreach{if{while}}` → cyclomatic 4, cognitive 6); a test where both agree would not detect a missing penalty.
- Boolean sequences: `a && b && c` (1) versus `a && b || c` (2) — the discriminating pair.
- `metric: "cognitive"` changes which items are returned and their order.
- Two projects sharing a file path neither lose nor duplicate rows.

## Out of scope (YAGNI)

- Configurable rule weights.
- Halstead metrics or maintainability index.
- Reporting lambdas as separate rows.
