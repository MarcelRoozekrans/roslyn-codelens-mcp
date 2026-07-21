# `get_instantiation_options` — design

**Goal:** answer "how do I construct this type" in one call — accessible constructors, static factory methods, and DI registrations.

Backlog item: `docs/BACKLOG.md` §5, medium tier. Pairs with `generate_test_skeleton`, which today guesses.

## Motivating defect

`GenerateTestSkeletonLogic.SutCreation` picks the fewest-parameter **public** constructor. When a type has none — the private-ctor-plus-static-factory pattern — `ctor is null` falls through to `return $"new {type.Name}()"`, emitting code that cannot compile. The tool has no way to discover the factory that would work.

## Tool contract

```
get_instantiation_options(
    symbol: string,                 // required
    fromProject: string?,           // optional caller context for accessibility
    includeInaccessible: bool = true,
    limit: int?)
```

Result sections: `constructors`, `factories`, `diRegistrations`, `requiredMembers`, plus `instantiable` + `note`.

## Probe findings (all verified against Roslyn 5.6, not assumed)

Probe source: scratchpad, reproduced in tests.

| Question | Finding |
|---|---|
| Record constructors | Gets an **implicit `protected Rec(Rec)` copy ctor**. Must be filtered — never a real construction option. |
| Struct constructors | Gets an **implicit `public S()`** even when none is declared. Must be **kept** — `new S()` is valid. |
| Plain class, no ctor declared | Same: implicit public parameterless. Keep, flag `isImplicit`. |
| Abstract / static / interface | `abstract` types **still expose constructors** (`protected AbsBase()`). Listing them as instantiable is wrong. Static classes and interfaces expose none. |
| Static factory detection | Static methods/properties/fields returning the type match cleanly. |
| Auto-property backing fields | `static X Instance { get; }` produces a static field `<Instance>k__BackingField` of self type — **would be reported as a construction option** unless `IsImplicitlyDeclared` members are excluded. |
| `Task<T>` / `ValueTask<T>` | Unwrap via `INamedTypeSymbol.ConstructedFrom`. |
| Generic `static T Make<T>()` | Return type is a `TypeParameter`; correctly does **not** match. Requires no special case. |
| `IsSymbolAccessibleWithin` | Honours `InternalsVisibleTo`: an `internal` ctor is `accessible=true` from an IVT'd `Tests` assembly, `false` from a stranger. **Throws** `ArgumentException` if `within` comes from an unrelated compilation — the context symbol must belong to the compilation being queried. |
| Obsolete | Detectable on both constructors and the type itself. |

## Rules

**Constructors** — from `INamedTypeSymbol.InstanceConstructors`:
- exclude the implicit record copy constructor (implicit, arity 1, parameter type == containing type);
- keep implicit parameterless constructors, flagged `isImplicit`;
- for `interface` / `static` / `abstract`, emit `instantiable: false` and a note instead of listing constructors. For abstract types the note points at `find_implementations`;
- report each parameter's type and name — `generate_test_skeleton` needs them to wire dependencies.

**Factories** — one `SolutionScanner` pass over the solution for static members whose (unwrapped) return type is the target:
- methods, properties, and fields; `IsImplicitlyDeclared` excluded (see backing-field finding);
- `Task<T>` / `ValueTask<T>` unwrapped;
- declared on **any** type, not just the target — the `WidgetFactory.Create()` pattern is the main case a self-only scan misses;
- **instance** methods returning the type (builder `Build()`) are deliberately excluded: the builder itself needs constructing, and recursing is unbounded;
- matched by **fully-qualified display string, not `SymbolEqualityComparer`** — the cross-compilation identity rule established by the exception-flow work.

**Accessibility** — declared accessibility always; `accessible` computed only when `fromProject` is given, via `IsSymbolAccessibleWithin` against a context symbol drawn from that project's own compilation.

**DI** — the scan moves out of `GetDiRegistrationsLogic` into a shared analyzer both tools call, and gains:
- `AddSingleton(typeof(IFoo), typeof(Foo))` typeof-pairs,
- single-generic `AddSingleton<Foo>()`,
- factory lambdas whose body is an object creation: `AddSingleton<IFoo>(sp => new Foo())`.

`get_di_registrations` output only grows; no existing result changes shape.

## Explicitly out of scope (YAGNI)

- Recursing into builder chains.
- Convention-based registration (Scrutor assembly scanning) — not statically resolvable.
- Ranking or recommending one option over another; the agent decides.

## Risks

Routing the DI scan through the deduping `SolutionScanner` is the exact change that silently broke `find_obsolete_usage` and `find_event_subscribers` (see `2026-07-21-scan-migration-design.md`). It is safe here because DI matching is **already string-based** rather than symbol-identity-based, so dedupe cannot cause the identity misses seen there. Pinned by a test with two projects sharing a file path — the case the old suite lacked.

## Testing

Discriminating cases, not just happy paths:
- record copy constructor absent;
- struct implicit parameterless constructor present;
- abstract type reports `instantiable: false` and no constructors;
- `<Instance>k__BackingField` absent from factories;
- the same `internal` constructor flips `accessible` between an IVT project and a stranger project;
- factory declared on a different type is found; instance `Build()` is not;
- `Task<T>`/`ValueTask<T>` factories unwrap;
- two projects sharing a file path do not lose or duplicate DI results.
