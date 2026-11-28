---
title: Inspect NuGet Packages
sidebar_position: 3
---

# Inspect NuGet Packages

Browse closed-source assemblies referenced by your solution — without decompiling manually.

## 1. Survey the assembly

```
Use inspect_external_assembly for Microsoft.Extensions.DependencyInjection.Abstractions
```

Returns the namespace tree and public type counts.

## 2. Drill into a namespace

```
Use inspect_external_assembly for Microsoft.Extensions.DependencyInjection.Abstractions with mode=namespace and namespaceFilter=Microsoft.Extensions.DependencyInjection
```

Returns all public types and their members in that namespace.

## 3. Peek at IL for a specific method

```
Use peek_il for IServiceCollection.Add
```

Returns the disassembled MSIL. Useful for understanding non-obvious behavior — e.g. whether an extension method is thread-safe.

:::note
`peek_il` works for NuGet packages restored to the local package cache. The assembly must be referenced by at least one project in the solution the call runs against: the active one, or the one named by `solution`.
:::
