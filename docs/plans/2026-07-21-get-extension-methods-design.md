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

### Resolved during implementation

- **`IPropertySymbol.ReduceExtensionMember(ITypeSymbol)` is the applicability primitive for extension properties** — the exact analogue of `ReduceExtensionMethod`, found by probing the public surface. Roslyn performs the generic inference: a property on `extension<T>(IEnumerable<T>)` reduces to `extension<char>` for `string` and `extension<int>` for `List<int>`, while one on `extension(IEnumerable<string>)` correctly returns null for both. The design had flagged "how to get the block's receiver type" as an open question; `INamedTypeSymbol.ExtensionParameter` does expose it, but using it would mean hand-rolling inference. **No such code exists in the implementation** — `ReduceExtensionMember` does the work.
- **`LanguageVersion.Preview` is NOT required.** Extension blocks parse under the compiler's default version — they are stable C# 14, not preview-gated. The probe that informed this design used `Preview` unnecessarily. The fixture still sets it explicitly to pin intent against SDK-default drift, but it is not load-bearing, and its verification test passes with or without.
- **`IsStatic` comes off the REDUCED symbol, and means "invoked on the type".** A block may declare `public static int Zero => 0;`, invoked as `int.Zero` rather than `value.Zero`, and a caller told only "`Zero` applies to `int`" would write the instance form and be wrong. The first implementation read `declaration.IsStatic` — which is **always true**, because every extension method is *declared* static; all 214 BCL entries for `List<int>` came back `IsStatic: true`, telling an agent to write `list.Where(…)` as `List<int>.Where(…)`. Probed: `ReduceExtensionMethod`/`ReduceExtensionMember` answer the question exactly — the reduced symbol is `IsStatic: false` for a classic `this int` extension and for a block *instance* member, `true` only for a block's own `static` member. One field read, both passes.
- **A block's static *methods* need pass B too; its instance methods must not be taken from it.** Probed on the container of `extension(int value) { … }`: an instance method `Thrice` is lifted with `IsExtensionMethod == true` (it has a classic call form), but a static method `MakeZero` is lifted with `IsExtensionMethod == false` — so pass A sees the first and never the second, and a properties-only pass B lost `MakeZero` entirely. Inside the nested `IsExtension` type, however, **both** appear with `IsExtensionMethod == false`, so "collect what pass A skipped" cannot be keyed on that flag without double-reporting `Thrice`. The discriminator is `IsStatic`: pass B takes all properties plus `MethodKind.Ordinary` methods that are static, and skips `get_`/`set_` accessors (reported as their property).
- **Candidate scope is per-compilation, and a metadata receiver has no single compilation.** `SymbolResolver`'s index is built over a *merged* `GlobalNamespace` (source plus referenced metadata) and dedupes by display string across compilations, so a type may be cached as seen from a *downstream* project. Resolving the receiver once and then locating its compilation therefore picks the wrong one and reports extensions that are not callable. The receiver is resolved independently inside each compilation. For a type the solution **declares**, the compilation declaring it in source wins — its reference closure is what is callable there. For a type the solution merely **references** (`string`, `int`, any BCL type) no compilation declares it, so the first-match fallback the first implementation used silently dropped the solution's own `Slugify(this string)` whenever it lived in any other project; every compilation that resolves the receiver now contributes, and the results are deduplicated (records are value-equal, and every project sees the same BCL). Self-limiting for constructed receivers: a compilation that cannot resolve `Widget` cannot resolve `List<Widget>` or `Widget[]` either, so it never contributes.
- **Receiver names are parsed by Roslyn, resolved by us.** `string[]`, `int?` and `(int, string)` are ordinary receivers — probed at 591 / 15 / 15 applicable members respectively — but the original hand-rolled name parser only understood keywords and `Name<…>` and threw `SymbolNotFound` for all three. `SyntaxFactory.ParseTypeName` supplies the shape (`ArrayTypeSyntax` with rank specifiers, `NullableTypeSyntax`, `TupleTypeSyntax`, `GenericNameSyntax`, …) and only the leaves go to the name lookup, which handles jagged and multidimensional arrays and any nesting for free. `SemanticModel.GetSpeculativeTypeInfo` was probed as the alternative and binds all three correctly, but it needs an invented call-site position and then only resolves names in scope *there* — it cannot resolve a bare `Widget` the way the solution-wide index does. Nullable reference annotations collapse (`string?` is `string`); only value types get `Nullable<T>`.
- **`Signature` leads with the return type for both kinds, and does not claim to be paste-ready.** Methods used to omit the return type while properties included it, so a listing showed `int Tripled` beside `Thrice()`. One `SymbolDisplayFormat` with `IncludeType` now renders both. The stronger promise — "what you actually type" — was dropped rather than engineered towards: a partially-inferred generic renders `Select<int, TResult>(Func<int, TResult>)`, and omitting the type-argument list would not make it compile either (`TResult` still appears in the parameters, and a signature names parameter *types*, never arguments), while it would throw away the useful half — that `TSource` is already pinned to `int` by the receiver.
- **Cost, measured:** 175 assemblies / 11,963 types / 1,403 static classes → **815 ms cold, 59 ms warm**, 293 applicable members for `List<int>`. `INamedTypeSymbol.MightContainExtensionMethods` gives a ~14× prune and is true for classes holding only extension *properties*, so it is safe for both passes. `IAssemblySymbol.MightContainExtensionMethods` is useless — true for all 175.

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

`type` takes the forms sibling tools accept: simple (`Widget`), fully qualified (`Demo.Widget`), constructed generic (`List<string>`), or a metadata type (`System.String`) — plus arrays (`string[]`, `int[,]`), nullables (`int?`) and tuples (`(int, string)`), since "what can I call on this array" is one of the questions the tool exists to answer. `nameFilter` is an optional case-insensitive substring on the member name — "is there a `Chunk` for this?" is a common shape and beats paging through everything.

## Discovery

For each candidate static class, two passes:

1. Members with `IsExtensionMethod` → `ReduceExtensionMethod(receiver)`. Non-null means applicable, and the reduced symbol is what the caller actually types. C# 14 block methods arrive here for free.
2. Nested types with `IsExtension` → their properties **and their static methods**, which pass 1 cannot see. Instance methods are skipped here: pass 1 already has them lifted onto the container. Applicability is decided against the block's receiver parameter type.

**For a type the solution declares, candidate scope is its own project plus that project's referenced assemblies** — an extension in an unreferenced project is not callable from there, so reporting it would be a false positive. **For a metadata receiver no project declares it**, so every compilation contributes and results are deduplicated.

## Result

Standard list envelope. `ExtensionMemberInfo { Name, Kind ("method" | "property"), Signature (the reduced, call-site form, return type first for both kinds), DeclaringType, Namespace, Origin ("source" | "metadata"), IsStatic (invoked on the type, not an instance), File?, Line?, XmlDocSummary? }`.

Sorted source-first, then by declaring type, then name. Summary: `{ byOrigin: { source, metadata }, byDeclaringType: { type: count } }`.

## Errors

Unresolvable type → `SymbolNotFound`. A namespace or other non-type symbol → `InvalidArgument`. `EnsureLoaded()` as usual.

## Known limits

Applicability is decided by receiver type alone: the tool cannot know whether a `using` is present at any particular call site, which is why the namespace is always reported. Anything `ReduceExtensionMethod` accepts is what the compiler accepts, so `ref`/`in` receivers and generic-constraint failures need no special handling. Extension methods reachable only through a project that does not reference the target type's project are deliberately excluded — which cannot apply to a metadata receiver, since no project declares one.

`Signature` is the call-site form, not paste-ready source: a partially inferred generic still names the type parameters the compiler infers from the arguments (`Select<int, TResult>(Func<int, TResult>)`).

## Testing

Matrix on `RenameTestWorkspace`: a simple `this int` extension applies to `int` and not to `string`; a generic `this IEnumerable<T>` applies to `List<int>` and to `string`, while `this IEnumerable<string>` does not apply to `string` (the probe's discriminating case); BCL LINQ appears for `List<int>` with `Origin: "metadata"`; a C# 14 block **method** is found, exactly once; a C# 14 block **property** is found with `Kind: "property"` — this is the test that fails if the `IsExtension` walk is dropped; a C# 14 block **static method** is found and marked `IsStatic`; classic, block-instance and BCL members are all marked `IsStatic: false`; array, nullable and tuple receivers resolve; signatures lead with the return type for both kinds; `nameFilter` narrows; source-first ordering; an extension in an unreferenced project is NOT reported while a genuinely cross-project one (extension in `Core`, receiver in `App`) IS, with `Core`'s internal container still hidden; a metadata receiver picks up extensions from a project nothing references; `SymbolNotFound` and `InvalidArgument` cases. Fixture integration on TestSolution.

The C# 14 tests need `LanguageVersion.Preview` on the parse options — verify `RenameTestWorkspace` can express that, and extend it if not.

## Docs

SKILL.md: Red Flags row ("what can I call on this type?" / "is there an extension for X?"), a tool bullet, Quick Reference row, metadata-support row (both source and metadata extensions are reported). CLAUDE.md 65 → **66**. `tools/DocGen/Program.cs` categoryMap → `navigation`. README bullet. BACKLOG: shipped + Recently-shipped row.
