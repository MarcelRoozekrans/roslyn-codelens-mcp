# `get_extension_methods` — Design

Date: 2026-07-21
Status: Approved
Origin: SharpLensMcp gap analysis (docs/BACKLOG.md §5, medium tier). "Which extension methods apply to this type" is not answerable with any current tool. Tool count 65 → 66.

## Probe findings (verified against Microsoft.CodeAnalysis 5.6, not assumed)

- **`IMethodSymbol.ReduceExtensionMethod(receiverType)` is the applicability primitive.** It returns null when the method does not apply and, when it does, returns the symbol *as called* — `IEnumerable<int>.Where<int>(Func<int,bool>)` rather than `Enumerable.Where<TSource>(IEnumerable<TSource>, Func<TSource,bool>)`. Generic inference is handled: `First2<T>(this IEnumerable<T>)` applies to `List<int>` and to `string` (which is `IEnumerable<char>`), while `Join2(this IEnumerable<string>)` correctly does **not** apply to `string`.
- **BCL LINQ works** — `Enumerable`'s 315 members reduce correctly against `List<int>`. An earlier probe suggested otherwise; that was a hand-picked, incomplete reference closure in the probe itself, not a Roslyn limitation. Worth recording, because designing around "LINQ is unavailable" would have produced a far worse tool.
- **C# 14 extension blocks split in two.** Given `extension(int value) { public int Tripled => …; public int Thrice() => …; }`:
  - the **method** `Thrice` is lifted onto the containing static class with `IsExtensionMethod == true`, so it flows through `ReduceExtensionMethod` transparently;
  - the **property** `Tripled` appears only as `get_Tripled` with `IsExtensionMethod == false`. A scan keyed on `IsExtensionMethod` misses extension properties entirely. They are reachable through the containing class's nested types, where `INamedTypeSymbol.IsExtension` is true (the nested type has an empty name).

## Decisions (brainstorming outcomes)

| Question | Decision |
|---|---|
| Search scope | **Solution source AND referenced metadata.** LINQ is the most common thing asked for; an answer omitting `Where`/`Select`/`Chunk` is worse than no answer because it looks complete. |
| C# 14 extension properties | **Included.** A tool answering "what can I call on this type" that silently omits them gives a confident wrong answer — the failure mode to avoid. |
| `using` scope | **Report everything applicable, with its namespace.** Filtering to what is already imported would need a call-site position the tool does not take, and would hide the very member being searched for. |
| Output | **Flat, standard envelope, source-first.** The solution's own handful of extensions is usually the answer; burying them under 200 BCL ones defeats the tool. |

## Tool contract

```
get_extension_methods(type: string, nameFilter?: string, limit?: int = 100)
```

`type` takes the forms sibling tools accept: simple (`Widget`), fully qualified (`Demo.Widget`), constructed generic (`List<string>`), or a metadata type (`System.String`). `nameFilter` is an optional case-insensitive substring on the member name — "is there a `Chunk` for this?" is a common shape and beats paging through everything.

## Discovery

For each candidate static class, two passes:

1. Members with `IsExtensionMethod` → `ReduceExtensionMethod(receiver)`. Non-null means applicable, and the reduced symbol is what the caller actually types. C# 14 block methods arrive here for free.
2. Nested types with `IsExtension` → their properties, which pass 1 cannot see. Applicability is decided against the block's receiver parameter type.

**Candidate scope is the target type's own project plus its referenced assemblies** — an extension in an unreferenced project is not callable from there, so reporting it would be a false positive.

## Result

Standard list envelope. `ExtensionMemberInfo { Name, Kind ("method" | "property"), Signature (the reduced, call-site form), DeclaringType, Namespace, Origin ("source" | "metadata"), File?, Line?, XmlDocSummary? }`.

Sorted source-first, then by declaring type, then name. Summary: `{ byOrigin: { source, metadata }, byDeclaringType: { type: count } }`.

## Errors

Unresolvable type → `SymbolNotFound`. A namespace or other non-type symbol → `InvalidArgument`. `EnsureLoaded()` as usual.

## Known limits

Applicability is decided by receiver type alone: the tool cannot know whether a `using` is present at any particular call site, which is why the namespace is always reported. Anything `ReduceExtensionMethod` accepts is what the compiler accepts, so `ref`/`in` receivers and generic-constraint failures need no special handling. Extension methods reachable only through a project that does not reference the target type's project are deliberately excluded.

## Testing

Matrix on `RenameTestWorkspace`: a simple `this int` extension applies to `int` and not to `string`; a generic `this IEnumerable<T>` applies to `List<int>` and to `string`, while `this IEnumerable<string>` does not apply to `string` (the probe's discriminating case); BCL LINQ appears for `List<int>` with `Origin: "metadata"`; a C# 14 block **method** is found; a C# 14 block **property** is found with `Kind: "property"` — this is the test that fails if the `IsExtension` walk is dropped; `nameFilter` narrows; source-first ordering; an extension in an unreferenced project is NOT reported; `SymbolNotFound` and `InvalidArgument` cases. Fixture integration on TestSolution.

The C# 14 tests need `LanguageVersion.Preview` on the parse options — verify `RenameTestWorkspace` can express that, and extend it if not.

## Docs

SKILL.md: Red Flags row ("what can I call on this type?" / "is there an extension for X?"), a tool bullet, Quick Reference row, metadata-support row (both source and metadata extensions are reported). CLAUDE.md 65 → **66**. `tools/DocGen/Program.cs` categoryMap → `navigation`. README bullet. BACKLOG: shipped + Recently-shipped row.
