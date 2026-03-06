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

### Finding Dependencies and Usage

- Use `find_callers` to find every call site for a method — more accurate than text search
- Use `find_implementations` to find all classes implementing an interface or extending a class
- Use `get_di_registrations` to see how types are wired in the DI container and their lifetimes
- Use `find_reflection_usage` to detect hidden/dynamic coupling that text search misses (Type.GetType, Activator.CreateInstance, MethodInfo.Invoke, assembly scanning)

### Planning Changes

Before modifying code, use these tools to understand the impact:

1. `get_symbol_context` — what does this type look like?
2. `find_callers` + `find_implementations` — what depends on it?
3. `get_type_hierarchy` + `get_project_dependencies` — where does it sit in the architecture?
4. `get_di_registrations` — how is it wired up?
5. `find_reflection_usage` — is it used dynamically?

Reference concrete types, interfaces, and call sites in your analysis. Example: "These 3 classes implement IUserService: UserService, CachedUserService, AdminUserService."

## Tool Quick Reference

| Tool | When to Use |
|------|-------------|
| `find_implementations` | "What implements this interface?" / "What extends this class?" |
| `find_callers` | "Who calls this method?" / "What depends on this?" |
| `get_type_hierarchy` | "What's the inheritance chain?" / "What are the extension points?" |
| `get_di_registrations` | "How is this wired up?" / "What's the DI lifetime?" |
| `get_project_dependencies` | "How do projects relate?" / "What's the dependency graph?" |
| `get_symbol_context` | "Give me everything about this type" (one-shot) |
| `find_reflection_usage` | "Is this used dynamically?" / "Are there hidden dependencies?" |
