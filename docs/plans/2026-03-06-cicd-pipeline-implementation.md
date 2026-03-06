# CI/CD Pipeline Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add fully automated CI/CD with GitVersion, Conventional Commits linting, and NuGet publishing to the Roslyn CodeGraph MCP Server.

**Architecture:** Three GitHub Actions workflows — PR validation (build + test + lint), release (version + pack + publish), and GitVersion config. NuGet package metadata added to the csproj. Branch protection configured manually.

**Tech Stack:** GitHub Actions, GitVersion, `amannn/action-semantic-pull-request`, .NET 9 SDK, NuGet.org

**Design Doc:** `docs/plans/2026-03-06-cicd-pipeline-design.md`

---

## Task 1: GitVersion Configuration

**Files:**
- Create: `GitVersion.yml`

**Step 1: Create GitVersion.yml**

```yaml
mode: ContinuousDeployment
branches:
  main:
    regex: ^main$
    tag: ''
    increment: Patch
  pull-request:
    regex: ^(pull|pull\-requests|pr)[/-]
    tag: preview
    increment: Inherit
```

**Step 2: Verify it's valid YAML**

```bash
cat GitVersion.yml
```

**Step 3: Commit**

```bash
git add GitVersion.yml
git commit -m "ci: add GitVersion configuration for semantic versioning"
```

---

## Task 2: NuGet Package Metadata

**Files:**
- Modify: `src/RoslynCodeGraph/RoslynCodeGraph.csproj`

**Step 1: Read the current csproj**

```bash
cat src/RoslynCodeGraph/RoslynCodeGraph.csproj
```

**Step 2: Add NuGet metadata to the first PropertyGroup**

Add these properties inside the existing `<PropertyGroup>` (after `ToolCommandName`):

```xml
<PackageId>RoslynCodeGraph</PackageId>
<Authors>Marcel Roozekrans</Authors>
<Description>Roslyn-based MCP server providing semantic code intelligence for .NET codebases</Description>
<PackageProjectUrl>https://github.com/MarcelRoozekrans/roslyn-codegraph-mcp</PackageProjectUrl>
<RepositoryUrl>https://github.com/MarcelRoozekrans/roslyn-codegraph-mcp</RepositoryUrl>
<PackageLicenseExpression>MIT</PackageLicenseExpression>
<PackageTags>roslyn;mcp;code-analysis;dotnet-tool</PackageTags>
<PackageReadmeFile>README.md</PackageReadmeFile>
```

Add a new ItemGroup for the README:

```xml
<ItemGroup>
  <None Include="../../README.md" Pack="true" PackagePath="/" />
</ItemGroup>
```

**Step 3: Verify it builds and packs**

```bash
dotnet build
dotnet pack src/RoslynCodeGraph/RoslynCodeGraph.csproj -o ./artifacts
```

Expected: Build succeeds, `.nupkg` file created in `./artifacts/`.

**Step 4: Clean up artifacts**

```bash
rm -rf ./artifacts
```

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/RoslynCodeGraph.csproj
git commit -m "ci: add NuGet package metadata to csproj"
```

---

## Task 3: CI Workflow (PR Validation)

**Files:**
- Create: `.github/workflows/ci.yml`

**Step 1: Create the workflow file**

```yaml
name: CI

on:
  pull_request:
    branches: [main]

permissions:
  pull-requests: read

jobs:
  lint-pr-title:
    name: Validate PR Title
    runs-on: ubuntu-latest
    steps:
      - uses: amannn/action-semantic-pull-request@v5
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        with:
          types: |
            feat
            fix
            chore
            docs
            refactor
            test
            ci
            perf

  build-and-test:
    name: Build & Test
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore

      - name: Test
        run: dotnet test --no-build --verbosity normal
```

**Step 2: Verify YAML is valid**

```bash
cat .github/workflows/ci.yml
```

**Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add PR validation workflow with build, test, and commit linting"
```

---

## Task 4: Release Workflow (Version, Pack, Publish)

**Files:**
- Create: `.github/workflows/release.yml`

**Step 1: Create the workflow file**

```yaml
name: Release

on:
  push:
    branches: [main]

permissions:
  contents: write

jobs:
  release:
    name: Build, Version & Publish
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Install GitVersion
        uses: gittools/actions/gitversion/setup@v3.1.11
        with:
          versionSpec: '6.x'

      - name: Calculate Version
        id: gitversion
        uses: gittools/actions/gitversion/execute@v3.1.11

      - name: Display Version
        run: echo "Version:${{ steps.gitversion.outputs.semVer }}"

      - name: Restore
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore -p:Version=${{ steps.gitversion.outputs.semVer }}

      - name: Test
        run: dotnet test --no-build --verbosity normal

      - name: Pack
        run: dotnet pack src/RoslynCodeGraph/RoslynCodeGraph.csproj --no-build -p:PackageVersion=${{ steps.gitversion.outputs.semVer }} -o ./artifacts

      - name: Push to NuGet
        if: env.NUGET_API_KEY != ''
        run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json --skip-duplicate
        env:
          NUGET_API_KEY: ${{ secrets.NUGET_API_KEY }}

      - name: Create Git Tag
        run: |
          git config user.name "github-actions[bot]"
          git config user.email "github-actions[bot]@users.noreply.github.com"
          git tag "v${{ steps.gitversion.outputs.semVer }}"
          git push origin "v${{ steps.gitversion.outputs.semVer }}"

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          tag_name: v${{ steps.gitversion.outputs.semVer }}
          name: v${{ steps.gitversion.outputs.semVer }}
          generate_release_notes: true
          files: ./artifacts/*.nupkg
```

**Step 2: Verify YAML is valid**

```bash
cat .github/workflows/release.yml
```

**Step 3: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: add release workflow with GitVersion, NuGet publish, and GitHub Release"
```

---

## Task 5: Add .gitignore entry for artifacts

**Files:**
- Modify: `.gitignore`

**Step 1: Add artifacts directory to .gitignore**

Append to `.gitignore`:

```
# Build artifacts
artifacts/
```

**Step 2: Commit**

```bash
git add .gitignore
git commit -m "chore: add artifacts/ to .gitignore"
```

---

## Task 6: Pin NuGet Package Versions

The csproj currently uses wildcard versions (`0.*`, `4.*`, etc.) which is fine for development but should be pinned for reproducible CI builds.

**Files:**
- Modify: `src/RoslynCodeGraph/RoslynCodeGraph.csproj`

**Step 1: Check current resolved versions**

```bash
dotnet list src/RoslynCodeGraph package
```

**Step 2: Replace wildcard versions with the resolved versions**

Update each `<PackageReference>` to use the exact resolved version from Step 1. For example, change `Version="0.*"` to `Version="0.1.0-preview.10"` (use whatever version was actually resolved).

**Step 3: Verify build still works**

```bash
dotnet build
dotnet test --verbosity quiet
```

Expected: Build succeeds, all tests pass.

**Step 4: Commit**

```bash
git add src/RoslynCodeGraph/RoslynCodeGraph.csproj
git commit -m "chore: pin NuGet package versions for reproducible builds"
```

---

## Task 7: Verify Full Pipeline Locally

**Step 1: Run all tests**

```bash
dotnet test --verbosity normal
```

Expected: All 16 tests pass.

**Step 2: Test pack with a version**

```bash
dotnet pack src/RoslynCodeGraph/RoslynCodeGraph.csproj -p:PackageVersion=0.0.1-local -o ./artifacts
```

Expected: `RoslynCodeGraph.0.0.1-local.nupkg` created.

**Step 3: Inspect the package**

```bash
dotnet nuget locals all --list
ls -la ./artifacts/
```

**Step 4: Clean up**

```bash
rm -rf ./artifacts
```

**Step 5: Verify git status is clean**

```bash
git status
```

Expected: Clean working tree.

---

## Summary

| Task | What | Files |
|------|------|-------|
| 1 | GitVersion config | `GitVersion.yml` |
| 2 | NuGet package metadata | `src/RoslynCodeGraph/RoslynCodeGraph.csproj` |
| 3 | CI workflow (PR validation) | `.github/workflows/ci.yml` |
| 4 | Release workflow (publish) | `.github/workflows/release.yml` |
| 5 | Gitignore artifacts | `.gitignore` |
| 6 | Pin NuGet versions | `src/RoslynCodeGraph/RoslynCodeGraph.csproj` |
| 7 | Local verification | — |

## Post-Implementation (Manual)

After pushing to GitHub, manually configure branch protection:
1. Go to repo Settings → Branches → Add rule for `main`
2. Check: Require pull request before merging
3. Check: Require status checks (select `build-and-test` and `lint-pr-title`)
4. Check: Require squash merging
5. Check: Automatically delete head branches
