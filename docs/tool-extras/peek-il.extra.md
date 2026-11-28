## Examples

**Disassemble a regular method:**
```
Use peek_il for JsonConvert.DeserializeObject
```

**Disassemble a constructor (use `.ctor`):**
```
Use peek_il for SqlOrderRepository..ctor
```

## When to use

Use `peek_il` when you need to understand what a closed-source method actually does — for example, to verify thread-safety, understand exception handling, or check whether a method has side effects not apparent from its signature.

## Notes

- Output is MSIL, not C#. For C# decompilation, use a tool like ILSpy or dnSpy directly.
- The assembly must be referenced by at least one project in the solution the call runs against: the active one, or the one named by `solution`.
- The IL cache is invalidated automatically when the DLL changes on disk.

## See also

- [`inspect_external_assembly`](/tools/inspect-external-assembly) — browse the public API surface
- [External Assemblies](/external-assemblies) — conceptual overview
