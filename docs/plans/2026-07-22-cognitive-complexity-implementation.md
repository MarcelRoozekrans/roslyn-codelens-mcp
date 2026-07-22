# Cognitive Complexity + Nesting Depth Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** report cognitive complexity and maximum nesting depth in `get_complexity_metrics`, correct three defects in the existing cyclomatic calculation, and move the scan onto `SolutionScanner`.

**Architecture:** A new pure `CognitiveComplexityCalculator` beside the existing `ComplexityCalculator`, both driven from one member walk in `GetComplexityMetricsLogic`. The model gains two fields; a new `metric` parameter selects which value drives filtering and sorting.

**Tech Stack:** C# 14 / net10.0, Microsoft.CodeAnalysis 5.6, xUnit.

**Design:** `docs/plans/2026-07-22-cognitive-complexity-design.md` — read it first, especially the defects table. Those numbers are probed facts, not estimates.

---

## Conventions you must follow

- Analysis primitives live in `src/RoslynCodeLens/Analysis/` as pure static classes over syntax nodes — no `LoadedSolution`, no semantic model unless genuinely needed.
- Solution-wide scans go through `SolutionScanner.EnumerateTrees`; read its XML docs before use.
- Tests target the logic/calculator, not the MCP wrapper.
- Run one test: `dotnet test --filter "FullyQualifiedName~<TestName>"`.
- Commit after each task.

## This change alters numbers a shipped tool already reports

That is approved and intended, but it means **downstream tests will move**. Consumers:
- `GetProjectHealthLogic` — calls `GetComplexityMetricsLogic.Execute(..., threshold: 10)`
- `FindUncoveredSymbolsLogic` — calls `ComplexityCalculator.Calculate` directly for its risk-hotspot count

Their tests are mostly *relational* (they compare against the underlying tool rather than hard-coded values), so most should follow automatically. **Do not "fix" a downstream test by loosening its assertion.** If one fails, work out whether the new number is correct and say so.

---

### Task 1: Correct the cyclomatic calculation

**Files:**
- Modify: `src/RoslynCodeLens/Analysis/ComplexityCalculator.cs`
- Test: `tests/RoslynCodeLens.Tests/Analysis/ComplexityCalculatorTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public void Calculate_ElseIfChain_CountsEachDecisionOnce()
{
    var method = ParseMethod(@"
        public int M(int a)
        {
            if (a == 1) return 1;
            else if (a == 2) return 2;
            else if (a == 3) return 3;
            else return 4;
        }");
    // 1 + three `if` decisions = 4.
    // Was 7: ElseClause was counted too, so each `else if` scored twice and
    // the bare `else` — which is no decision at all — scored as well.
    Assert.Equal(4, ComplexityCalculator.Calculate(method));
}

[Fact]
public void Calculate_BareElse_IsNotADecision()
{
    var method = ParseMethod("public int M(bool a) { if (a) return 1; else return 2; }");
    Assert.Equal(2, ComplexityCalculator.Calculate(method)); // was 3
}

[Fact]
public void Calculate_SwitchExpression_CountsArmsExceptDiscard()
{
    var method = ParseMethod("public int M(int a) => a switch { 1 => 1, 2 => 2, _ => 0 };");
    // 1 + two real arms = 3. Was 1 — switch expressions were invisible.
    Assert.Equal(3, ComplexityCalculator.Calculate(method));
}

[Fact]
public void Calculate_SwitchStatement_CountsCasesExceptDefault()
{
    var method = ParseMethod(@"
        public int M(int a)
        {
            switch (a)
            {
                case 1: return 1;
                case 2: return 2;
                default: return 0;
            }
        }");
    // 1 + two cases = 3. Was 4: `default` was counted as a decision.
    Assert.Equal(3, ComplexityCalculator.Calculate(method));
}

[Fact]
public void Calculate_MultipleLabelsOnOneSection_CountsEachLabel()
{
    var method = ParseMethod(@"
        public int M(int a)
        {
            switch (a)
            {
                case 1:
                case 2: return 1;
                default: return 0;
            }
        }");
    // Two case labels share one SwitchSection but are two decisions.
    Assert.Equal(3, ComplexityCalculator.Calculate(method));
}
```

**Step 2: Run — expect FAIL** with the old values (7, 3, 1, 4, 2 respectively).

**Step 3: Implement**

- Remove `SyntaxKind.ElseClause` from the counted kinds.
- Replace `SyntaxKind.SwitchSection` with **`CaseSwitchLabelSyntax` / `CasePatternSwitchLabelSyntax`** counting (so multi-label sections count correctly and `default:` — a `DefaultSwitchLabelSyntax` — does not).
- Add `SwitchExpressionArmSyntax`, excluding an arm whose pattern is a `DiscardPatternSyntax`.

Keep everything else exactly as it is, including the `&&`/`||`/`??` token pass.

**Step 4: Run — expect PASS.**

**Step 5: Update the existing test that encoded the bug**

`Calculate_NestedIfElseAndLoop_CountsAll` asserts 5 and its comment says `+ else`. The corrected value is **4**. Update the number and the comment; do not delete the test.

**Step 6: Run the whole calculator suite, then commit**

```bash
dotnet test --filter "FullyQualifiedName~ComplexityCalculatorTests"
git commit -m "fix(complexity): count each decision once (else-if, switch expressions, default)"
```

---

### Task 2: Cognitive complexity calculator

**Files:**
- Create: `src/RoslynCodeLens/Analysis/CognitiveComplexityCalculator.cs`
- Test: `tests/RoslynCodeLens.Tests/Analysis/CognitiveComplexityCalculatorTests.cs`

**Step 1: Write the failing tests**

These come from the SonarSource specification. Use these numbers, not arithmetic of your own.

```csharp
[Fact]
public void Nesting_IsPenalised()
{
    var method = ParseMethod(@"
        public void M(int[] xs)
        {
            foreach (var x in xs)      // +1 (nesting 0)
                if (x > 0)             // +2 (nesting 1)
                    while (x > 1)      // +3 (nesting 2)
                        System.Console.Write(x);
        }");
    // 6 — while cyclomatic scores this 4. This pair is the whole point of the
    // metric: a test where both agree cannot detect a missing nesting penalty.
    Assert.Equal(6, CognitiveComplexityCalculator.Calculate(method));
    Assert.Equal(4, ComplexityCalculator.Calculate(method));
}

[Fact]
public void ElseIf_AddsOne_ButNoNestingPenalty()
{
    var method = ParseMethod(@"
        public int M(int a)
        {
            if (a == 1) return 1;      // +1
            else if (a == 2) return 2; // +1
            else return 3;             // +1
        }");
    Assert.Equal(3, CognitiveComplexityCalculator.Calculate(method));
}

[Theory]
[InlineData("a && b && c", 1)]        // one sequence
[InlineData("a && b || c", 2)]        // two sequences
[InlineData("a && b && c || d", 2)]
[InlineData("a || b", 1)]
public void BooleanSequences_CountOncePerSequence(string expr, int expected)
{
    var method = ParseMethod($"public bool M(bool a, bool b, bool c, bool d) => {expr};");
    Assert.Equal(expected, CognitiveComplexityCalculator.Calculate(method));
}

[Fact]
public void Lambda_RaisesNesting_ButDoesNotScoreItself()
{
    var method = ParseMethod(@"
        public void M(System.Collections.Generic.List<int> xs)
        {
            xs.ForEach(x => { if (x > 0) System.Console.Write(x); });
        }");
    // The lambda itself is +0; the `if` inside it sits at nesting 1, so +2.
    Assert.Equal(2, CognitiveComplexityCalculator.Calculate(method));
}

[Fact]
public void LocalFunction_RaisesNesting_ButDoesNotScoreItself()
{
    var method = ParseMethod(@"
        public void M()
        {
            void Inner(int y) { if (y > 0) System.Console.Write(y); }
            Inner(1);
        }");
    Assert.Equal(2, CognitiveComplexityCalculator.Calculate(method));
}

[Fact]
public void Catch_IsPenalisedByNesting()
{
    var method = ParseMethod(@"
        public void M()
        {
            try { }
            catch (System.Exception) { }   // +1
            finally { }                    // finally is not a branch
        }");
    Assert.Equal(1, CognitiveComplexityCalculator.Calculate(method));
}

[Fact]
public void Switch_ScoresOnce_NotPerCase()
{
    var method = ParseMethod(@"
        public int M(int a)
        {
            switch (a)                 // +1 for the whole switch
            {
                case 1: return 1;
                case 2: return 2;
                default: return 0;
            }
        }");
    // Cognitive treats one switch as ONE decision to understand, unlike cyclomatic.
    Assert.Equal(1, CognitiveComplexityCalculator.Calculate(method));
}

[Fact]
public void Goto_AddsOne()
{
    var method = ParseMethod(@"
        public void M(int a)
        {
            if (a > 0) goto End;    // +1 if, +1 goto
            End: ;
        }");
    Assert.Equal(2, CognitiveComplexityCalculator.Calculate(method));
}

[Fact]
public void DirectRecursion_AddsOne()
{
    var method = ParseMethod("public int F(int n) => n < 2 ? n : F(n - 1) + F(n - 2);");
    // +1 ternary, +1 recursion (counted once however many recursive calls).
    Assert.Equal(2, CognitiveComplexityCalculator.Calculate(method));
}

[Fact]
public void TrivialMethod_IsZero()
{
    // Cognitive complexity starts at ZERO, unlike cyclomatic's 1 — a method with
    // no branching costs nothing to understand.
    Assert.Equal(0, CognitiveComplexityCalculator.Calculate(ParseMethod("public void M() { }")));
}
```

**Step 2: Run — FAIL** (class does not exist).

**Step 3: Implement**

A recursive walk carrying a `nesting` level:

- **+1 + nesting** for: `IfStatementSyntax` (when it is not an `else if` — i.e. its parent is not an `ElseClauseSyntax`), `ConditionalExpressionSyntax`, `SwitchStatementSyntax`, `SwitchExpressionSyntax`, `ForStatementSyntax`, `ForEachStatementSyntax`, `WhileStatementSyntax`, `DoStatementSyntax`, `CatchClauseSyntax`.
- **+1, no nesting penalty** for: `ElseClauseSyntax` (both a bare `else` and an `else if`), `GotoStatementSyntax`, and `break`/`continue` carrying a label.
- **Nesting increases** when descending into any of the structures above, and also into lambdas (`SimpleLambdaExpressionSyntax`, `ParenthesizedLambdaExpressionSyntax`, `AnonymousMethodExpressionSyntax`) and `LocalFunctionStatementSyntax` — those two raise nesting but score nothing themselves.
- **Boolean sequences:** +1 for each logical binary node whose parent is *not* a binary node of the same kind. Verified against Roslyn's tree: `a && b && c` yields three nodes but one sequence; `a && b || c` yields two sequences.
- **Recursion:** +1 once if the member invokes itself by name. Match on the identifier only — a semantic model is not available here, and the false-positive risk (a same-named method on another type) is acceptable for a heuristic metric. **Document that.**

`MaxNesting` is exposed by the same walk: add `public static int MaxNesting(SyntaxNode node)` or return both from one method — your choice, but compute them in **one** traversal, not two.

**Step 4: Run — PASS.**

**Step 5: Commit** `feat(complexity): cognitive complexity calculator`.

---

### Task 3: Max nesting depth

**Files:** as Task 2.

**Step 1: Failing tests**

```csharp
[Theory]
[InlineData("public void M() { }", 0)]
[InlineData("public void M(bool a) { if (a) { } }", 1)]
[InlineData("public void M(bool a, bool b) { if (a) { if (b) { } } }", 2)]
public void MaxNesting_MeasuresDeepestControlStructure(string code, int expected)
    => Assert.Equal(expected, CognitiveComplexityCalculator.MaxNesting(ParseMethod(code)));

[Fact]
public void MaxNesting_CountsLambdaBodies()
{
    var method = ParseMethod(@"
        public void M(System.Collections.Generic.List<int> xs)
        {
            xs.ForEach(x => { if (x > 0) { } });
        }");
    Assert.Equal(1, CognitiveComplexityCalculator.MaxNesting(method));
}
```

**Steps 2-5:** as before. Commit `feat(complexity): report max nesting depth`.

---

### Task 4: Model + member discovery

**Files:**
- Modify: `src/RoslynCodeLens/Models/ComplexityMetric.cs`
- Modify: `src/RoslynCodeLens/Tools/GetComplexityMetricsLogic.cs`
- Test: `tests/RoslynCodeLens.Tests/Tools/GetComplexityMetricsToolTests.cs`

**Step 1: Extend the model**

```csharp
/// <param name="Complexity">
/// Cyclomatic. Keeps its original name so existing consumers stay shape-compatible.
/// </param>
public record ComplexityMetric(
    string MethodName,
    string TypeName,
    int Complexity,
    int Cognitive,
    int MaxNesting,
    string File,
    int Line,
    string Project);
```

Fix every construction site the compiler flags.

**Step 2: Failing test for the missing members**

```csharp
[Fact]
public void Execute_ReportsConstructorsAndProperties()
{
    // Only MethodDeclarationSyntax was visited, so a complex constructor or property
    // was invisible however bad it was.
    var results = GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 0, ComplexityMetricKind.Cyclomatic);
    Assert.Contains(results, r => r.MethodName is ".ctor" or "Greeter");
    Assert.Contains(results, r => r.MethodName == "FormalNameLength");
}
```

**Step 3: Implement**

Walk `MemberDeclarationSyntax` covering methods, constructors, destructors, operators, conversion operators, indexers and properties. For a property with accessors, report the property once using the **maximum** across its accessors; for an expression-bodied property use its expression.

Name a constructor after its type (matching how other tools render it) and say so in a comment.

**Step 4-5:** run, commit `feat(complexity): report constructors, properties, indexers and operators`.

---

### Task 5: `metric` parameter

**Files:** `GetComplexityMetricsLogic.cs`, `GetComplexityMetricsTool.cs`, tests.

Add `public enum ComplexityMetricKind { Cyclomatic, Cognitive }`.

- `threshold` filters on the selected metric; the worst-first sort uses it too.
- Default is `Cyclomatic`, so `get_project_health` and every existing caller are unaffected.
- Summary (`max`/`avg`/`overThreshold`) is computed over the selected metric; add `maxCognitive` alongside so the other value is still visible.

**Test:** a method whose two scores differ (the `foreach{if{while}}` shape: cyclomatic 4, cognitive 6) is returned at `threshold: 5, metric: cognitive` and **not** at `threshold: 5, metric: cyclomatic`. That single test pins both filtering and the selection.

Commit `feat(complexity): metric parameter selecting cyclomatic or cognitive`.

---

### Task 6: Migrate to SolutionScanner

**Files:** `GetComplexityMetricsLogic.cs`, tests.

Replace the hand-rolled `foreach (compilation) foreach (tree)` with `SolutionScanner.EnumerateTrees`. Keep generated code **excluded** (the default) — nobody refactors a generated file.

**This is the class of change that silently broke `find_obsolete_usage` and `find_event_subscribers`.** Read `docs/plans/2026-07-21-scan-migration-design.md` before starting. It is lower risk here because rows are keyed by file and line with no cross-compilation symbol identity — but prove it rather than assuming:

```csharp
[Fact]
public void Two_projects_sharing_a_file_path_do_not_lose_or_duplicate_rows()
{
    // Complexity is a per-file fact, not a per-project one: the same file linked into
    // two projects is the same code, so it should be reported ONCE.
    // Decide the scopeDiscriminator from this test's outcome, not from reasoning.
}
```

Run it **20 times** and report the ratio. The earlier migration failed on 9 of 20 runs, so one green run is not evidence.

Also confirm the `project` filter still works — it must move into `projectFilter`, not stay in the loop body (see the scanner's XML docs on why a body-level `continue` can silently drop a linked file).

Commit `refactor(complexity): scan via SolutionScanner`.

---

### Task 7: Downstream consumers

Run the full suite and inspect **every** failure in `GetProjectHealthToolTests` and `FindUncoveredSymbolsToolTests`.

For each: decide whether the new number is correct, and only then update the test — with a comment recording the old value and why it changed. If a downstream number changed in a way you cannot justify, **stop and report it**; that is a real regression, not a test to adjust.

Commit `test: follow corrected complexity numbers downstream`.

---

### Task 8: Docs

- `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` — describe both metrics, when each is the better signal, and the `metric` parameter.
- `README.md` — update the `get_complexity_metrics` line.
- `docs/BACKLOG.md` — move the cognitive-complexity item to shipped; **this closes the SharpLens gap analysis**. Record the recursion heuristic's false-positive caveat under deferred items.
- Tool `[Description]` must state that `complexity` is cyclomatic and that cognitive starts at 0 while cyclomatic starts at 1 — otherwise a reader will think a 0 is a bug.

Run `dotnet test --filter "FullyQualifiedName~ToolDescriptionMdxSafety"`.

Commit `docs: document cognitive complexity (closes the SharpLens gap list)`.

---

### Task 9: Verify

```bash
dotnet build
dotnet test
git status --short tests/RoslynCodeLens.Tests/Fixtures/   # must be empty
```

Then a high-effort review before the PR. Specifically re-check:

1. **Does each new test fail when its rule is removed?** Vacuous tests have shipped here twice.
2. **Is the nesting penalty actually applied**, or does the cognitive score merely happen to match on the chosen examples? The cyclomatic-disagrees case is the one that proves it.
3. **Did any downstream number change silently** rather than being asserted?
4. Run the shared-path scanner test 20× — not once.
