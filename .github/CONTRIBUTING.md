# Contributing to RoslynCodeLens MCP

## Conventional Commits

This project uses [conventional commits](https://www.conventionalcommits.org/). All commit messages must follow this format:

```
<type>(<scope>): <description>

[optional body]
```

### Types

| Type | Description |
|------|-------------|
| `feat` | New MCP tool or feature |
| `fix` | Bug fix |
| `perf` | Performance improvement |
| `docs` | Documentation changes |
| `test` | Adding or updating tests |
| `refactor` | Code refactoring (no behavior change) |
| `ci` | CI/CD pipeline changes |
| `chore` | Maintenance tasks |

### Examples

```
feat: add find_dead_code MCP tool
fix(find_callers): handle partial methods correctly
perf(search_symbols): use span-based iteration
docs: update README with new tool descriptions
test: add tests for rebuild_solution
ci: add NuGet artifact upload to CI
```

## Adding a New MCP Tool

1. **Create the logic** in `src/RoslynCodeLens/Tools/<Name>Logic.cs`
2. **Create the tool wrapper** in `src/RoslynCodeLens/Tools/<Name>Tool.cs`
3. **Write tests** in `tests/RoslynCodeLens.Tests/Tools/<Name>ToolTests.cs`
4. **Update SKILL.md** in `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
5. **Update README.md** with the new tool
6. **Run benchmarks** if performance-relevant

### Conventions

- Logic and tool wrapper are always separate files
- Tools use `[McpServerToolType]` and `[McpServerTool]` attributes
- Logic classes are `internal static` with a single `Execute`/`ExecuteAsync` method
- All tools receive `SolutionManager` via DI

### Testing

```bash
dotnet test
```

Tests use xUnit with `IAsyncLifetime` for solution loading. The test fixture at `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/` provides a multi-project solution for integration tests.

## Building on Windows 11 with Smart App Control

[Smart App Control](https://support.microsoft.com/en-US/Windows/Security/Threat-Malware-Protection/smart-app-control-has-blocked-part-of-this-app) blocks binaries that are both unsigned and low-reputation. The `ZeroAlloc.Analyzers` package currently meets both criteria, so SAC may block it during a build. If you hit this, opt out of that analyzer:

```bash
dotnet build -p:EnableZeroAllocAnalyzers=false
dotnet test -p:EnableZeroAllocAnalyzers=false
```

Only the analyzer is skipped; nothing else about the build changes. The single suppression that depends on its rules (`ZA0601` in `src/RoslynCodeLens/SolutionLoader.cs`) is inert without it.

## Releasing

Releases are automatic on merge to `main`:
- GitVersion calculates the version from commit history
- NuGet package is built and published
- Git tag and GitHub Release are created with auto-generated notes
