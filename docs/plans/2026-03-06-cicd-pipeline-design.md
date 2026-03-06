# CI/CD Pipeline — Design Document

**Date:** 2026-03-06
**Status:** Approved

## Problem Statement

The project has no build pipeline, no automated release, and no commit linting. Releases to NuGet.org must be manual. This design adds full CI/CD automation.

## Solution

Three GitHub Actions workflows with GitVersion for semantic versioning, Conventional Commits enforcement via PR title linting, and automated NuGet publishing.

## Versioning

- **GitVersion** calculates the next version from git history + commit messages
- Conventional Commits prefixes drive version bumps:
  - `feat:` → minor (1.0.0 → 1.1.0)
  - `fix:` → patch (1.0.0 → 1.0.1)
  - `BREAKING CHANGE` in body → major (1.0.0 → 2.0.0)
- No manual version in `.csproj` — injected at build time via `/p:Version=X.Y.Z`
- Tags created automatically by the release workflow

### GitVersion Configuration

`GitVersion.yml` at repo root:
```yaml
workflow: ContinuousDeployment
branches:
  main:
    regex: ^main$
    label: ''
    increment: Patch
  pull-request:
    regex: ^(pull|pull\-requests|pr)[/-]
    label: preview
    increment: Inherit
```

## Commit Linting

- **`amannn/action-semantic-pull-request`** validates PR titles
- Squash merge enforced — PR title becomes the commit message on main
- No local tooling (no Node.js, no husky)
- Allowed types: `feat`, `fix`, `chore`, `docs`, `refactor`, `test`, `ci`, `perf`

## Workflows

### 1. `ci.yml` — Pull Request Validation

**Trigger:** `pull_request` targeting `main`

**Steps:**
1. Checkout with full history (`fetch-depth: 0`)
2. Setup .NET 10 SDK
3. Validate PR title (Conventional Commits)
4. `dotnet restore`
5. `dotnet build --no-restore`
6. `dotnet test --no-build`

**Runner:** `ubuntu-latest`

### 2. `release.yml` — Build, Version, Publish

**Trigger:** `push` to `main`

**Steps:**
1. Checkout with full history
2. Setup .NET 10 SDK
3. Install GitVersion
4. Calculate version → `$GITVERSION_SEMVER`
5. `dotnet restore`
6. `dotnet build --no-restore -p:Version=$GITVERSION_SEMVER`
7. `dotnet test --no-build`
8. `dotnet pack --no-build -p:PackageVersion=$GITVERSION_SEMVER -o ./artifacts`
9. `dotnet nuget push ./artifacts/*.nupkg --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json`
10. Create git tag `v$GITVERSION_SEMVER`
11. Create GitHub Release with tag, attach `.nupkg`

**Runner:** `ubuntu-latest`
**Secrets:** `NUGET_API_KEY`

### 3. Branch Protection (manual setup)

- Require PR for pushes to `main`
- Require `ci` status check to pass
- Squash merge only
- Delete head branches after merge

## NuGet Package Metadata

Add to `RoslynCodeGraph.csproj`:
```xml
<PropertyGroup>
  <PackageId>RoslynCodeGraph</PackageId>
  <Authors>Marcel Roozekrans</Authors>
  <Description>Roslyn-based MCP server providing semantic code intelligence for .NET codebases</Description>
  <PackageProjectUrl>https://github.com/MarcelRoozekrans/roslyn-codegraph-mcp</PackageProjectUrl>
  <RepositoryUrl>https://github.com/MarcelRoozekrans/roslyn-codegraph-mcp</RepositoryUrl>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageTags>roslyn;mcp;code-analysis;dotnet-tool</PackageTags>
  <PackageReadmeFile>README.md</PackageReadmeFile>
</PropertyGroup>

<ItemGroup>
  <None Include="../../README.md" Pack="true" PackagePath="/" />
</ItemGroup>
```

## Files

```
.github/
  workflows/
    ci.yml
    release.yml
GitVersion.yml
```

## Out of Scope

- Multi-platform CI matrix (Linux-only for now)
- Changelog generation (can add later)
- Pre-release NuGet packages from PRs
