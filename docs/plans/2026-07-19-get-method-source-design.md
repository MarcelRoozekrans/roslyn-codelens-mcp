# `get_method_source` — Design

Date: 2026-07-19
Status: Approved
Origin: SharpLensMcp gap analysis (docs/BACKLOG.md §5, high value #3). Returns member source bodies by name so agents stop `Read`ing whole files; `analyze_method` gives signature + callers but not the body. SharpLens ships scalar + batch tools; we ship ONE tool with array input.

## Decisions (brainstorming outcomes)

| Question | Decision |
|---|---|
| Symbol kinds (v1) | Members only: methods (all overloads expand to separate items), constructors, properties/indexers (whole declaration incl. accessors), fields, events. Whole types excluded — `get_type_overview` + `Read` cover that; type bodies defeat the token-efficiency goal. |
| Batch shape | One tool, `symbols: string[]` (one or many), standard list envelope, items in request order. No separate batch tool. |
| Error semantics | Per-item: name problems never throw. Statuses `ok / notFound / ambiguous (with candidates) / metadata / unsupportedKind`. Only an empty `symbols` array throws `InvalidArgument`. |
| Source extraction | Slice original syntax: `DeclaringSyntaxReferences` → node `ToFullString()` (leading trivia = XML docs + attributes, original formatting preserved). Rejected: `NormalizeWhitespace` reformatting. |

## Tool contract

```
get_method_source(
  symbols: string[],   // "MyClass.MyMethod", "Namespace.MyClass.MyMethod", ctor as "MyClass.MyClass"
  limit?: int          // envelope limit, default 100
)
```

Standard envelope; items in request order; overload groups expand adjacently. Summary: `{ byStatus: { ok, notFound, ambiguous, metadata, unsupportedKind } }`.

## Item model — `MemberSourceInfo`

`RequestedSymbol`, `Status`, `Symbol` (resolved display, null unless resolved), `Kind` (`method | constructor | property | indexer | field | event`), `File`, `StartLine`, `EndLine`, `Source`, `Project`, `Candidates` (`IReadOnlyList<string>?`, ambiguous only).

## Resolution & extraction

- `SymbolResolver.FindSymbols` per requested name (same contract as sibling tools, arity-stripped index applies).
- Overload group (same containing type + name, all methods) → one `ok` item per overload. Cross-type matches → `ambiguous` with candidate display strings (reuse the grouping semantics of `RenameSymbolLogic.ResolveSingleTarget` — extract/share if clean, else mirror).
- Constructor request form `Type.TypeName` (member segment == type simple name): resolve the type, return `InstanceConstructors` + `StaticConstructors` that have source, one item each, `Kind=constructor`.
- Resolved symbol is a type → `unsupportedKind` (message points at `get_type_overview` / `Read`).
- Metadata-only symbol → `metadata` (message points at `peek_il` / `inspect_external_assembly`).
- Extraction per declaration reference: node from `DeclaringSyntaxReferences`; for field/event variable declarators walk up to the containing `FieldDeclarationSyntax`/`EventFieldDeclarationSyntax`; source = `node.ToFullString()` trimmed of leading/trailing blank lines; `File`/`StartLine`/`EndLine` from the node's (non-full) span. Partial members: one item per declaration part.

## Error handling

Empty `symbols` → `InvalidArgument`. Everything else per-item. `manager.EnsureLoaded()` as usual.

## Testing

RenameTestWorkspace matrix: XML doc + attribute included in `Source`; overload expansion (2 items, request order); property with accessors; expression-bodied member; field with initializer (declaration statement returned); event; ctor via `Widget.Widget` (instance + static when present); partial method two parts (two items, distinct files); `notFound`; `ambiguous` with candidates; metadata (`System.String.Concat`); type name → `unsupportedKind`; request-order preservation across mixed statuses; empty array throws. One TestSolution fixture test asserting `Greeter.Greet`'s real body text. `ToolDescriptionMdxSafetyTests` covers the new description automatically.

## Docs

- SKILL.md: Red Flags row ("Let me `Read` the file to see this method's body" → `get_method_source`), pre-Read checklist item update, bullet in Understanding a Codebase, Quick Reference row, metadata-support row (`No — source only; metadata members reported with status "metadata"`).
- CLAUDE.md 59 → 60. tools/DocGen categoryMap: `get_method_source` → `analysis` (lesson from #301: uncategorized pages + MDX). BACKLOG §5 bullet → shipped on merge + Recently shipped row.
