---
name: roslyn-codegraph
description: Use Roslyn-powered semantic code intelligence when working with .NET codebases. Activates automatically when roslyn-codegraph MCP tools are available.
---

# Roslyn Code Graph Intelligence

## Detection

Check if `find_implementations` is available as an MCP tool. If not, this skill is inert — do nothing.

## When to Use These Tools

Use these tools **instead of Grep/Glob** whenever you need to understand .NET code structure. They provide semantic accuracy that text search cannot match.

### Understanding a Codebase

- Call `get_project_dependencies` to understand solution architecture and how projects relate
- Call `get_symbol_context` on types mentioned in the user's request for a full context dump (namespace, base class, interfaces, DI dependencies, public members)
- Call `get_type_hierarchy` to understand inheritance chains and extension points

### Navigating Code

- Use `go_to_definition` to jump to where a type or member is defined — faster than file search
- Use `search_symbols` to fuzzy-find types, methods, properties, and fields by name
- Use `find_references` to find every reference to a symbol across the entire solution

### Finding Dependencies and Usage

- Use `find_callers` to find every call site for a method — more accurate than text search
- Use `find_implementations` to find all classes implementing an interface or extending a class
- Use `get_di_registrations` to see how types are wired in the DI container and their lifetimes
- Use `find_reflection_usage` to detect hidden/dynamic coupling that text search misses (Type.GetType, Activator.CreateInstance, MethodInfo.Invoke, assembly scanning)
- Use `get_nuget_dependencies` to see what NuGet packages a project uses and their versions
- Use `find_attribute_usages` to find all types/members decorated with a specific attribute (e.g., Obsolete, Authorize, Serializable)

### Diagnosing Issues

- Use `get_diagnostics` to list compiler errors, warnings, and Roslyn analyzer diagnostics across the solution
- Use `get_code_fixes` to get structured text edits for fixing a specific diagnostic — review and apply via Edit tool

### Code Quality Analysis

- Use `find_unused_symbols` to detect dead code — types/members with no references
- Use `get_complexity_metrics` to find overly complex methods (cyclomatic complexity above threshold)
- Use `find_naming_violations` to check .NET naming convention compliance (PascalCase, camelCase, I-prefix, _ prefix)
- Use `find_large_classes` to identify types that may need refactoring (too many members or lines)
- Use `find_circular_dependencies` to detect project or namespace dependency cycles

### Source Generators

- Use `get_source_generators` to list source generators and their output per project (optional project filter)
- Use `get_generated_code` to inspect generated source code from source generators (filter by generator name or file path)

### Planning Changes

Before modifying code, use these tools to understand the impact:

1. `get_symbol_context` — what does this type look like?
2. `find_references` + `find_callers` + `find_implementations` — what depends on it?
3. `get_type_hierarchy` + `get_project_dependencies` — where does it sit in the architecture?
4. `get_di_registrations` — how is it wired up?
5. `find_reflection_usage` — is it used dynamically?
6. `find_attribute_usages` — are there attribute-driven behaviors (authorization, serialization, etc.)?
7. `get_diagnostics` — are there existing compiler/analyzer warnings to address?
8. `get_code_fixes` — can any warnings be auto-fixed?
9. `find_unused_symbols` — is there dead code to clean up?
10. `get_complexity_metrics` — are there overly complex methods?

Reference concrete types, interfaces, and call sites in your analysis. Example: "These 3 classes implement IUserService: UserService, CachedUserService, AdminUserService."

## Tool Quick Reference

| Tool | When to Use |
|------|-------------|
| `find_implementations` | "What implements this interface?" / "What extends this class?" |
| `find_callers` | "Who calls this method?" / "What depends on this?" |
| `find_references` | "Where is this symbol used?" / "Show all references" |
| `go_to_definition` | "Where is this defined?" / "Jump to source" |
| `search_symbols` | "Find types/methods matching this name" |
| `get_type_hierarchy` | "What's the inheritance chain?" / "What are the extension points?" |
| `get_symbol_context` | "Give me everything about this type" (one-shot) |
| `get_di_registrations` | "How is this wired up?" / "What's the DI lifetime?" |
| `get_project_dependencies` | "How do projects relate?" / "What's the dependency graph?" |
| `get_nuget_dependencies` | "What packages does this project use?" |
| `find_reflection_usage` | "Is this used dynamically?" / "Are there hidden dependencies?" |
| `find_attribute_usages` | "What's marked [Obsolete]?" / "Find all [Authorize] controllers" |
| `get_diagnostics` | "Any compiler errors?" / "Show warnings for this project" |
| `get_code_fixes` | "How do I fix this warning?" / "Get auto-fix suggestions" |
| `find_unused_symbols` | "Any dead code?" / "What's not being used?" |
| `get_complexity_metrics` | "Which methods are too complex?" |
| `find_naming_violations` | "Check naming conventions" |
| `find_large_classes` | "Find classes that need splitting" |
| `find_circular_dependencies` | "Any circular dependencies?" |
| `get_source_generators` | "What source generators are active?" / "List generators for this project" |
| `get_generated_code` | "Show generated code" / "What did this generator produce?" |
