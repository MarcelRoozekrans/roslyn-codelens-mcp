# Feature Analysis & Adoption Roadmap

> Competitive analysis against [sharplens-mcp](https://github.com/pzalutski-pixel/sharplens-mcp), a reference Roslyn MCP server implementation (~58 tools).

---

## 1. Project Comparison Summary

| Dimension | sharplens-mcp | roslyn-codelens-mcp |
|-----------|---------------|----------------------|
| Tools | ~58 | 54 |
| Architecture | Monolithic `RoslynService.cs` (~5 000 lines) | Modular Tool + Logic split per feature |
| API paradigm | Position-based `(filePath, line, col)` | Symbol-name-based (`"IGreeter.Greet"`) |
| Refactoring | 13 dedicated write/mutate tools | `get_code_actions` + `apply_code_action` (generic engine) |
| Code generation | Dedicated tools (null checks, equality, ctor) | Via Roslyn code actions (no extra tools) |
| Compound/batch | 6 tools | 3 (`get_type_overview`, `analyze_method`, `get_file_overview`) |
| Multi-solution | Env var only | Built-in manager with `list_solutions` / `set_active_solution` |
| Hot reload | Manual `sync_documents` tool | `FileChangeTracker` — automatic on file change |
| Roslyn version | 5.0.0 | 4.14.0 |
| .NET target | net8.0 | net10.0 |
| Benchmarks | None | BenchmarkDotNet suite (31 benchmarks) |
| Tests | Unknown | xUnit suite per tool |

### Why direct timing benchmarks are not meaningful

The two projects have fundamentally different API paradigms:

- **sharplens-mcp** requires `(filePath, line, column)` coordinates — you must already know where a symbol appears in source before calling any tool.
- **roslyn-codelens-mcp** takes symbol names (`"IGreeter"`, `"Greeter.Greet"`) — the server resolves them via pre-built indexes.

This means the tools serve different workflows and cannot be given identical inputs. Additionally, sharplens-mcp targets .NET 8 while this project targets .NET 10, so any raw latency delta would conflate runtime differences with implementation differences. A qualitative feature comparison (below) is the honest comparison.

---

## 2. Feature Gap Analysis

### 2.1 Tools We Already Have (Parity or Better)

| Their Tool | Our Equivalent | Notes |
|------------|---------------|-------|
| `health_check` | (implicit) | Server status |
| `load_solution` | `list_solutions` / `set_active_solution` | We support multi-solution |
| `get_symbol_info` | `get_symbol_context` | |
| `go_to_definition` | `go_to_definition` | |
| `find_references` | `find_references` | |
| `find_implementations` | `find_implementations` | |
| `find_callers` | `find_callers` | |
| `get_type_hierarchy` | `get_type_hierarchy` | |
| `search_symbols` | `search_symbols` | |
| `get_diagnostics` | `get_diagnostics` | |
| `get_code_fixes` | `get_code_fixes` | |
| `get_complexity_metrics` | `get_complexity_metrics` | |
| `find_unused_code` | `find_unused_symbols` | |
| `get_project_structure` | `get_project_dependencies` | |
| `get_attributes` | `find_attribute_usages` | |
| `get_derived_types` | `get_type_hierarchy` | Included in hierarchy |
| `get_base_types` | `get_type_hierarchy` | Included in hierarchy |
| `get_code_actions_at_position` | `get_code_actions` | **Implemented — Phase 1 complete** |
| `apply_code_action_by_title` | `apply_code_action` | **Implemented — Phase 1 complete** |

### 2.2 Remaining Gaps

All previously identified gaps have been closed. See Section 4 for phase status.

#### Potential future additions

| Tool | Description | Why Valuable |
|------|-------------|-------------|
| `validate_code` | Syntax + compilation check without full build | Quick validation after edits |
| `get_outgoing_calls` | Methods called by a given method (standalone) | `analyze_method` includes this; standalone useful for large methods |

### 2.3 Tools We Have That They Don't

| Our Tool | Description |
|----------|-------------|
| `find_circular_dependencies` | Detect circular project references |
| `find_large_classes` | Find classes exceeding size thresholds |
| `find_naming_violations` | Check .NET naming convention compliance |
| `find_reflection_usage` | Find reflection API usage |
| `get_di_registrations` | Find dependency injection registrations |
| `get_generated_code` | Show source-generator output |
| `get_source_generators` | List active source generators |
| `get_nuget_dependencies` | List NuGet package references |
| `inspect_external_assembly` | Browse API surface of referenced closed-source assemblies (NuGet/internal DLLs) |
| `peek_il` | Read raw IL for metadata methods — understand external behavior without decompilation |
| `rebuild_solution` | Force full reload |
| `list_solutions` / `set_active_solution` | Multi-solution management |

### 2.4 Full Feature Matrix vs sharplens-mcp

| Capability | sharplens-mcp | roslyn-codelens-mcp | Notes |
|------------|:---:|:---:|-------|
| Go to definition | ✅ | ✅ | |
| Find references | ✅ | ✅ | |
| Find implementations | ✅ | ✅ | |
| Find callers | ✅ | ✅ | |
| Get type hierarchy | ✅ | ✅ | |
| Symbol info / context | ✅ | ✅ | |
| Search symbols | ✅ | ✅ | |
| Get diagnostics | ✅ | ✅ | |
| Code fixes | ✅ | ✅ | |
| Complexity metrics | ✅ | ✅ | |
| Find unused symbols | ✅ | ✅ | |
| Project structure | ✅ | ✅ | |
| Find attribute usages | ✅ | ✅ | |
| Get derived types | ✅ | ✅ | Included in `get_type_hierarchy` |
| Get base types | ✅ | ✅ | Included in `get_type_hierarchy` |
| Code actions at position | ✅ | ✅ | |
| Apply code action | ✅ | ✅ | We add preview/diff mode |
| Rename symbol | ✅ | ✅ | Via `apply_code_action` |
| Extract method | ✅ | ✅ | Via `apply_code_action` |
| Inline variable | ✅ | ✅ | Via `apply_code_action` |
| Implement interface members | ✅ | ✅ | Via `apply_code_action` |
| Generate constructor | ✅ | ✅ | Via `apply_code_action` |
| Add null checks | ✅ | ✅ | Via `apply_code_action` |
| Generate Equals/GetHashCode | ✅ | ✅ | Via `apply_code_action` |
| Encapsulate field | ✅ | ✅ | Via `apply_code_action` |
| Analyze data flow | ❌ | ✅ | Variables declared/read/written/captured |
| Analyze control flow | ❌ | ✅ | Reachability, return/exit points |
| Analyze change impact | ❌ | ✅ | Blast radius for any symbol change |
| Get type overview (compound) | ❌ | ✅ | Context + hierarchy + diagnostics in one call |
| Analyze method (compound) | ❌ | ✅ | Signature + callers + outgoing calls |
| Get file overview (compound) | ❌ | ✅ | Types defined + file diagnostics |
| DI registrations | ❌ | ✅ | Scan DI service wiring and lifetimes |
| NuGet dependencies | ❌ | ✅ | Per-project package references |
| Source generators | ❌ | ✅ | List generators and inspect output |
| Generated code | ❌ | ✅ | Inspect generator output source |
| Circular dependencies | ❌ | ✅ | Detect project/namespace cycles |
| Naming violations | ❌ | ✅ | .NET naming convention compliance |
| Find large classes | ❌ | ✅ | Types exceeding member/line thresholds |
| Find reflection usage | ❌ | ✅ | Dynamic/reflection coupling detection |
| Rebuild solution | ❌ | ✅ | Force full reload + index rebuild |
| Multi-solution management | ❌ | ✅ | `list_solutions` / `set_active_solution` |
| External assembly browsing | ❌ | ✅ | `inspect_external_assembly` — summary + namespace drill-down |
| Metadata symbol resolution | ❌ | ✅ | All Tier-1 tools accept fully-qualified external symbol names |
| IL disassembly | ❌ | ✅ | `peek_il` — ilasm text for any referenced method |
| Automatic hot reload | ❌ | ✅ | `FileChangeTracker` — no manual sync needed |
| Symbol-name API | ❌ | ✅ | No coordinates needed; works without open editor |
| Preview mode for edits | ❌ | ✅ | Diff returned before writing to disk |
| BenchmarkDotNet suite | ❌ | ✅ | 31 benchmarks covering all tools |

---

## 3. Implementation Patterns Worth Adopting

### 3.1 Refactoring via Roslyn Code Actions ✅ Done

Load `CodeRefactoringProvider` and `CodeFixProvider` from `Microsoft.CodeAnalysis.CSharp.Features` via reflection. Two generic tools cover all built-in Roslyn refactorings (rename, extract method, inline, encapsulate field, etc.) without implementing each one individually. Preview mode returns a diff before writing.

### 3.2 Compound/Batch Tools

Combine multiple queries into one response to reduce round-trips and token usage:
- `get_type_overview` = type info + members + hierarchy + diagnostics
- `analyze_method` = signature + callers + outgoing calls

### 3.3 Data Flow Analysis

Uses `SemanticModel.AnalyzeDataFlow(node)` — variables declared, read, written, captured by lambdas.

### 3.4 Control Flow Analysis

Uses `SemanticModel.AnalyzeControlFlow(statements)` — entry/exit points, unreachable statements, return/break/continue points.

### 3.5 Change Impact Analysis

Transitive `find_references` + `find_callers` — direct references, affected callers, types, and projects.

---

## 4. Recommended Next Phases

| Phase | What | Status |
|-------|------|--------|
| 1 | Generic refactoring engine (`get_code_actions` + `apply_code_action`) | ✅ Complete |
| 2 | Flow analysis (`analyze_data_flow`, `analyze_control_flow`) | ✅ Complete |
| 3 | Impact analysis (`analyze_change_impact`) | ✅ Complete |
| 4 | Compound tools (`get_type_overview`, `analyze_method`, `get_file_overview`) | ✅ Complete |
| 5 | Code generation (`implement_missing_members`, `generate_constructor`) | N/A — covered by `apply_code_action` (see SKILL.md) |

---

## 5. Architecture Notes

### Patterns to Avoid (from reference implementation)

- Monolithic service file (~5000 lines) — all tool implementations in one place
- No provider caching — reflection runs per-call
- Switch-based routing — brittle, no auto-discovery
- Manual document sync — agents must call `sync_documents` after edits

### Our Advantages to Preserve

- **Modular Tool + Logic split** — each tool independently testable
- **Multi-solution manager** — unique to this project
- **FileChangeTracker** — automatic hot-reload vs manual sync
- **Domain-specific tools** — DI registrations, source generators, NuGet deps, circular deps, naming violations
