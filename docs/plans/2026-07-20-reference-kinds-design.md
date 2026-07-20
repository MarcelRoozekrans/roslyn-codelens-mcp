# `find_references` Reference-Kind Classification — Design

Date: 2026-07-20
Status: Approved
Origin: SharpLensMcp gap analysis (docs/BACKLOG.md §5, high value #4). Enhance the existing `find_references` tool to tag each reference with a precise kind and accept a server-side kind filter. Subsumes a would-be `find_pattern_usages` (is/as/pattern-match sites become `type_check`). Not a new tool.

## Current state

`find_references` (`FindReferencesLogic`) classifies via a shallow parent switch into `assignment / argument / type_constraint / base_type / instantiation / type_argument / usage`, dedupes to **one item per `(file, line)`** (collapsing multiple refs on a line), has no filter, and its summary carries `byProject` only. `SymbolReference` = `(ReferenceKind, File, Line, Snippet, Project, IsGenerated)`. The only other consumer is `analyze_change_impact`, which carries `SymbolReference` through without inspecting `ReferenceKind`.

## Decisions (brainstorming outcomes)

| Question | Decision |
|---|---|
| Vocabulary | **Replace** the coarse strings with one clean taxonomy (below). Breaking change to output, documented in PR + SKILL. |
| Granularity | **Per-occurrence**: add a `Column` field, dedupe by `(file, line, column)`. `x = x + 1` → a `write` and a `read`. Raises `totalCount` vs the old per-line merge. |
| Read/write engine | Syntax-parent walk (Roslyn `IsWrittenTo` pattern) for assign/compound/`++`/`--`/`out`; per-document **semantic model** (cached, not per-ref) only for implicit `ref`/`out` parameter resolution. |
| Filter | Optional `kinds: string[]`; server-side, applied before the limit; unknown kind → `InvalidArgument` listing valid kinds. |

## Taxonomy

`usage` is retained only as a rare fallback for genuinely unclassifiable nodes.

**Value references** (locals, fields, properties, parameters, events):
`read`, `write` (assignment LHS, `out` argument), `readwrite` (compound assignment, `++`/`--`, `ref` argument), `invocation` (method called), `method_group` (method as delegate value, not invoked).

**Type references:**
`object_creation` (`new Foo()`), `cast` (`(Foo)x`, `x as Foo`), `type_check` (`x is Foo`, `x is Foo f`, `case Foo f:`, `Foo f =>` — pattern subsumption), `typeof`, `base_type` (base list), `type_constraint` (`where T : Foo`), `type_argument` (`List<Foo>`, method type args), `declaration` (variable/parameter/return/field/using-alias type positions), `attribute` (`[Foo]`).

**Any symbol:** `nameof` (`nameof(X)`), `xml_doc` (`<see cref="Foo"/>`).

## Classification algorithm (`ReferenceClassifier`, pure static)

Input: the `SyntaxNode` at the reference location + the reference's `ISymbol` (from Roslyn's `ReferencedSymbol`) + a lazy `Func<SemanticModel>` for the document.

1. **nameof** first: if any ancestor is an `InvocationExpressionSyntax` whose `Expression` is `IdentifierName "nameof"` and the reference is inside its argument list → `nameof`.
2. **xml cref**: ancestor `XmlCrefAttributeSyntax` / `CrefSyntax` → `xml_doc`.
3. **Type-symbol references** (`ISymbol` is `INamedTypeSymbol`/type, or the node is in type position): ascend to the governing `TypeSyntax` and switch on its context — attribute, object-creation, cast (`CastExpressionSyntax` or `BinaryExpression`/`as`), `type_check` (`IsPatternExpression`/`is`/declaration- or type- or recursive-pattern/case type), `typeof`, base list, constraint clause, type-argument list; otherwise `declaration`.
4. **Value-symbol references**: compute the *effective expression* by climbing member-access `.Name`, element-access, and parenthesized wrappers to the outermost expression whose value is this reference, then:
   - method symbol invoked (`InvocationExpression.Expression`) → `invocation`; method symbol as a value → `method_group`.
   - effective expr is assignment `Left`: simple `=` → `write`; compound → `readwrite`.
   - parent is `++`/`--` (pre/post) → `readwrite`.
   - `ArgumentSyntax`: `out` keyword → `write`; `ref` keyword → `readwrite`; no keyword but the resolved parameter `RefKind` (via semantic model) is `Out` → `write`, `Ref` → `readwrite`; else → `read`.
   - default → `read`.
5. Anything unmatched → `usage`.

Type-vs-value routing keys off the reference `ISymbol.Kind` primarily (robust for `nameof`/cref where syntax is ambiguous), falling back to node shape.

## Data flow & dedup

`ScanForReferences` keeps Roslyn's per-occurrence `ReferenceLocation`s (they are already distinct by span); the existing cross-target `seen` set is re-keyed to `(file, line, column)` where `column = span.StartLinePosition.Character + 1`. Each surviving location → classify → `SymbolReference(kind, file, line, column, snippet, project, isGenerated)`. Filtering (`kinds`) and `byKind` summary happen in the tool wrapper on the classified list, before sort/limit; sort becomes `(file, line, column)`.

## Model & tool changes

- `SymbolReference` gains `int Column` (positional, after `Line`). Single construction site is `FindReferencesLogic`; `analyze_change_impact` reuses that output, so no other call site changes. JSON is name-serialized — field order is not a wire concern.
- `find_references` gains `[Description] string[]? kinds = null`. Description enumerates the taxonomy. `byKind` added to the summary.
- `analyze_change_impact`: unaffected functionally; its `DirectReferences` now carry precise kinds for free.

## Error handling

Empty result set is normal (not an error). `kinds` containing an unknown value → `McpToolException(InvalidArgument, "...", details=validKinds)`. `EnsureLoaded()` as usual.

## Testing

`ReferenceClassifierTests` (pure, `RenameTestWorkspace`) — a matrix with one fixture exercising every kind: field read/write/readwrite (`_x`, `_x =`, `_x +=`, `_x++`, `ref`/`out` args), property get/set, method invocation vs method-group (`Select(Handler)`), `new`, cast + `as`, `is`/`is T v`/switch pattern, `typeof`, base type, constraint, `List<T>` arg, `nameof`, `[Attr]`, `<see cref>`, declaration positions. `FindReferencesLogic` tests: same-line multi-ref (`x = x + 1`) yields two items with distinct columns/kinds; dedup across partial-type targets. Tool tests: `kinds` filter narrows results + `totalCount`, unknown kind throws, `byKind` summary correct. Update existing `find_references` tests that assert old kind strings. Fixture integration on TestSolution. `ToolDescriptionMdxSafetyTests` covers the new description.

## Docs

- SKILL.md: `find_references` "Response shape" summary line gains `byKind`; the tool's Red Flags/Quick Reference entries note kind classification + `kinds` filter; add a short kind-vocabulary reference block; the "Are we leaking event subscriptions / who mutates this?" style questions point at `kinds: ["write","readwrite"]`. The `find_pattern_usages` idea is explicitly folded in (`type_check`).
- CLAUDE.md tool count unchanged (still an enhancement, not a new tool — 60).
- docs/BACKLOG.md §5: mark the reference-kind bullet ✅ shipped; no Recently-shipped table row (that table is for new tools) — instead note it as a shipped enhancement inline.
- No DocGen category change (existing tool).
