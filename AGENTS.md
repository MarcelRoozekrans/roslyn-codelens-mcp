# AGENTS.md: roslyn-codelens-mcp

This is a Roslyn-based MCP server that provides semantic code intelligence for .NET codebases. It ships
as the .NET global tool `RoslynCodeLens.Mcp` (command `roslyn-codelens-mcp`, built from
`src/RoslynCodeLens`), an npm launcher, a Docker image and an agent plugin with a skill.

## Layout

- `src/RoslynCodeLens/` — MCP server (entry point: Program.cs). It targets `net10.0` and `net11.0`.
- `tests/RoslynCodeLens.Tests/` — Unit tests (xUnit). Targets `net10.0` and `net11.0`.
  `Fixtures/` holds nested solutions the tests load with Roslyn. They are excluded from the test
  project's compile, restored on demand by `FixtureRestore.cs`, and all target `net10.0`.
- `benchmarks/RoslynCodeLens.Benchmarks/` — BenchmarkDotNet performance tests. Targets `net10.0` only.
- `plugins/roslyn-codelens/` — agent skill definition
- `tools/DocGen/` — generates the tool reference pages of the docs site. It targets `net11.0` only and
  is not in `RoslynCodeLens.slnx`, which holds only `src`, `tests` and `benchmarks`.
- `Directory.Build.props` applies the analyzer set to every project. There is no tracked `global.json`.

## MCP Server

The `.mcp.json` configures the roslyn-codelens MCP server to analyze this solution. It runs via `dotnet run` and connects over stdio.

## Skill

The `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md` skill teaches an agent when and how to use the 67 code intelligence tools. Use the MCP tools instead of Grep/Glob for any .NET semantic queries (finding implementations, callers, references, diagnostics, etc.).

## Build and test

Prerequisites: the .NET 10 and .NET 11 SDKs. Running the tests needs the .NET 10 and .NET 11 runtimes,
because the test project targets both. The benchmarks need the .NET 10 runtime, DocGen the .NET 11 one.

Development:

- Build: `dotnet build`
- Test: `dotnet test`, after the fixture `global.json` and the four fixture restores from the CI
  block below. Without that SDK pin the Razor fixtures get no generated code on a host with the
  .NET 11 SDK ([#560](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/560)).
- Benchmarks: `dotnet run --project benchmarks/RoslynCodeLens.Benchmarks -c Release`

CI job `build` in `.github/workflows/ci.yml` (name "Build & Test"):

```bash
dotnet tool restore
dotnet restore
# Pins the fixtures to the .NET 10 SDK (issue 560). The file is gitignored.
echo '{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }' > tests/RoslynCodeLens.Tests/Fixtures/global.json
dotnet restore tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestSolution.slnx
dotnet restore tests/RoslynCodeLens.Tests/Fixtures/FilterableSolution/FilterableSolution.slnx
dotnet restore tests/RoslynCodeLens.Tests/Fixtures/RazorFixture/RazorFixture.slnx
dotnet restore tests/RoslynCodeLens.Tests/Fixtures/PropsFixture/PropsFixture.slnx
dotnet build --no-restore -c Release
dotnet test --no-build -c Release --verbosity normal --collect "XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
```

The CI job then runs the `coverage-gate` action of ci-baseline on `TestResults/**/coverage.cobertura.xml`.
It merges the reports and fails below a line-coverage floor. The commands above do not reproduce that
gate. Pass `--collect` and `--settings` together, or coverage hits are lost.

CI job `build` in `.github/workflows/docs-build.yml` (docs site, needs Node):

```bash
dotnet run --project tools/DocGen -- --output docs/site/docs/tools --extras docs/tool-extras
cd docs/site && npm ci && npm run build
```

`baseline-checks.yml` calls the shared ci-baseline workflow on `RoslynCodeLens.slnx`: gitleaks, CodeQL
and NuGetAudit.

## Conventions and gotchas

- The tool count appears in `SKILL.md`. A tool is a `[McpServerTool(Name = "...")]` method under
  `src/RoslynCodeLens/Tools/`; count them with
  `grep -rhE '^\s*\[McpServerTool\(' src/RoslynCodeLens/Tools | wc -l` when it changes.
- `ZeroAlloc.Analyzers` in `Directory.Build.props` can be switched off with
  `-p:EnableZeroAllocAnalyzers=false`. That is for Windows 11 Smart App Control, [#369](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/issues/369). CI leaves
  it on.
- A Debug build on Windows stops a running `RoslynCodeLens` server from this checkout, so the output
  files can be overwritten. Opt out with `-p:StopLockingServerOnBuild=false`.
- The `Microsoft.Build*` packages in the server and benchmark projects use `ExcludeAssets="runtime"`
  on purpose, because MSBuildLocator needs them excluded. Do not remove it.
- The `Dockerfile` publishes `net11.0` only and runs on the SDK image, not the runtime image, because
  MSBuildLocator needs a real MSBuild.

<!-- org-core:start sha256=08a88996bc9165f888fc0260607cfd0670cae128c323e16707c6b452ee66222d (generated from docker-nuc scripts/agents-md/org-core-personal.md; do not edit here) -->
## Org rules

These rules hold in every public MarcelRoozekrans repo.

### Commits and releases

- Conventional Commits. release-please reads them on the default branch, and the changelog sections
  in `release-please-config.json` decide which types count. The standard: `feat`, `fix`, `perf`,
  `deps` and `revert` open or update the release PR; `docs`, `test`, `refactor`, `build`, `ci`,
  `chore` and `style` do not, unless the commit is breaking or has a `Release-As:` footer. The
  release happens when that PR merges. A breaking change needs `!` or a `BREAKING CHANGE:` footer.
- A commit body never contains nested parentheses. release-please cannot parse them and silently
  drops the whole commit from the changelog, while the workflow still reports success.
- After merging a commit that should release, check that it appears in the release-please PR.

### Branches and pull requests

- Every change goes through a branch and a pull request. Never push to the default branch directly.
- A PR body says what the diff does, and nothing the diff does not do.
- Fix review findings in the PR under review, including small ones. File an issue only for a finding
  that belongs to other code, needs a design decision, is its own piece of work, or waits on
  something external.

### CI

- The .NET repos call the shared workflows of `MarcelRoozekrans/ci-baseline`, pinned to `@v1`, never
  `@main`.
- Its coverage gate fails the build when line coverage drops below the floor of 80%, and Codecov
  fails a PR that lowers project coverage by more than 1 percentage point. New code comes with
  tests.
- A repo with a lower floor documents the reason and links the issue that raises it.
<!-- org-core:end -->
