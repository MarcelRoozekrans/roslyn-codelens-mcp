## Examples

**Add a parameter to a method:**
```
Use change_signature on OrderService.Submit: add `CancellationToken ct` with callSiteValue `CancellationToken.None`
```

**Add a parameter to a positional record:**
```
Use change_signature on Shipment: add `string Carrier` with callSiteValue `"unknown"`
```

## Constructors

A type name (`Shipment`, `Ns.Shipment`) or the constructor form `Shipment.Shipment` targets a constructor:

- the primary constructor, when the type has one: a positional `record`, a `record struct`, or a C# 12 class or struct with a parameter list;
- otherwise the only constructor declared in source.

A type with several declared constructors and no primary one is reported as ambiguous, with each signature listed. Every `new Shipment(...)` and every chained `this(...)` initializer is rewritten.

## Warnings on positional records

A record's parameter list also declares its generated properties and its `Deconstruct`. Roslyn rewrites the construction sites, but not what depends on those members, so the result's `Warnings` names:

- each **removed generated property** with its references, `with` initializers included, at `file:line`;
- each **deconstruction, positional pattern and direct `Deconstruct` call** that binds the record by position, at `file:line`. After an add or remove they stop compiling. After a **reorder of two parameters of the same type** they still compile, but bind the other value, so no conflict check can catch them;
- the **text** the change leaves alone: string literals, JSON or other serialized property names, and comments.

Read `Warnings` in preview before applying. `Warnings` is empty for methods and for non-record constructors.

## See also

- [`rename_symbol`](/tools/rename-symbol), which shares the preview, conflict and freshness gates
- [`find_references`](/tools/find-references)
