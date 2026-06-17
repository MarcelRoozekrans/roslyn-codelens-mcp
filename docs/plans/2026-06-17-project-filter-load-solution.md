# Project-filter for `load_solution` — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add optional `include` (glob) and `rootProjects` (exact-name) parameters to `load_solution` so users can load a transitively-complete subset of a large `.sln` instead of the whole thing.

**Architecture:** A pure-logic seed-and-closure layer (`ProjectFilter`, `ProjectGraphReader`, `ClosureWalker`) sits in front of `SolutionLoader.OpenAsync`. When no filter is passed the existing `workspace.OpenSolutionAsync` path runs unchanged. When a filter is passed, we read each `.csproj`'s `ProjectReference` elements via lightweight XML parse, BFS-walk from the seed set, then open only the closure-included projects via `workspace.OpenProjectAsync`. The filter threads through `MultiSolutionManager → SolutionManager → SolutionLoader`; reloading the same `path` with a different filter disposes the previous workspace (replace semantics).

**Tech Stack:** C# / .NET 10, xUnit, Roslyn `Microsoft.CodeAnalysis.MSBuild`, ModelContextProtocol.Server.

**Design doc:** [`docs/plans/2026-06-17-project-filter-load-solution-design.md`](2026-06-17-project-filter-load-solution-design.md).

---

## Task 1: Define `ProjectFilter` value type

**Files:**
- Create: `src/RoslynCodeLens/ProjectFilter.cs`

**Step 1: Write the failing test**

Create `tests/RoslynCodeLens.Tests/ProjectFilterTests.cs`:

```csharp
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class ProjectFilterTests
{
    [Fact]
    public void HasSeeds_ReturnsFalse_WhenBothEmpty()
    {
        var filter = new ProjectFilter(Array.Empty<string>(), Array.Empty<string>());
        Assert.False(filter.HasSeeds);
    }

    [Fact]
    public void HasSeeds_ReturnsTrue_WhenIncludeNonEmpty()
    {
        var filter = new ProjectFilter(new[] { "App.*" }, Array.Empty<string>());
        Assert.True(filter.HasSeeds);
    }

    [Fact]
    public void HasSeeds_ReturnsTrue_WhenRootProjectsNonEmpty()
    {
        var filter = new ProjectFilter(Array.Empty<string>(), new[] { "App.Api" });
        Assert.True(filter.HasSeeds);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ProjectFilterTests" --nologo`
Expected: FAIL with `CS0246: The type or namespace name 'ProjectFilter' could not be found`.

**Step 3: Write minimal implementation**

`src/RoslynCodeLens/ProjectFilter.cs`:

```csharp
namespace RoslynCodeLens;

/// <summary>
/// Optional input to <see cref="SolutionLoader.OpenAsync(string, ProjectFilter?, System.Threading.CancellationToken)"/>.
/// <see cref="Include"/> and <see cref="RootProjects"/> together act as the
/// seed set; the loader walks <c>ProjectReference</c> transitively from
/// these seeds to produce the loaded project set.
/// </summary>
/// <param name="Include">Glob patterns matched against <c>Project.Name</c>.</param>
/// <param name="RootProjects">Exact project names; missing names are an error.</param>
public sealed record ProjectFilter(
    IReadOnlyList<string> Include,
    IReadOnlyList<string> RootProjects)
{
    public bool HasSeeds => Include.Count > 0 || RootProjects.Count > 0;
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ProjectFilterTests" --nologo`
Expected: PASS (3 tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/ProjectFilter.cs tests/RoslynCodeLens.Tests/ProjectFilterTests.cs
git commit -m "feat(loader): introduce ProjectFilter value type"
```

---

## Task 2: `ProjectGraphReader` — parse `<ProjectReference>` from a csproj

**Files:**
- Create: `src/RoslynCodeLens/ProjectGraphReader.cs`
- Create: `tests/RoslynCodeLens.Tests/ProjectGraphReaderTests.cs`
- Create: `tests/RoslynCodeLens.Tests/Fixtures/GraphReader/ProjectWithRefs.csproj`
- Create: `tests/RoslynCodeLens.Tests/Fixtures/GraphReader/Sdk_NoRefs.csproj`
- Create: `tests/RoslynCodeLens.Tests/Fixtures/GraphReader/Malformed.csproj`

**Step 1: Create fixture csprojs**

`tests/RoslynCodeLens.Tests/Fixtures/GraphReader/Sdk_NoRefs.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

`tests/RoslynCodeLens.Tests/Fixtures/GraphReader/ProjectWithRefs.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\App.Domain\App.Domain.csproj" />
    <ProjectReference Include="..\Shared.Common\Shared.Common.csproj" />
  </ItemGroup>
</Project>
```

`tests/RoslynCodeLens.Tests/Fixtures/GraphReader/Malformed.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="" />
```

(intentionally truncated — closing tags missing)

Also add these to the test csproj so they copy to output. Add to `tests/RoslynCodeLens.Tests/RoslynCodeLens.Tests.csproj` under the existing `<ItemGroup>` for fixture content (search for existing `<None Include="Fixtures/**/*"`; if present add an entry, else add a new include block):

```xml
<ItemGroup>
  <None Include="Fixtures\GraphReader\**\*.csproj">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

**Step 2: Write failing tests**

`tests/RoslynCodeLens.Tests/ProjectGraphReaderTests.cs`:

```csharp
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class ProjectGraphReaderTests
{
    private static string FixturePath(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "GraphReader", fileName);

    [Fact]
    public void ReadProjectReferences_ReturnsAbsolutePaths()
    {
        var refs = ProjectGraphReader.ReadProjectReferences(FixturePath("ProjectWithRefs.csproj"));

        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.True(Path.IsPathRooted(r)));
        Assert.Contains(refs, r => r.EndsWith("App.Domain.csproj"));
        Assert.Contains(refs, r => r.EndsWith("Shared.Common.csproj"));
    }

    [Fact]
    public void ReadProjectReferences_ReturnsEmpty_WhenNoRefs()
    {
        var refs = ProjectGraphReader.ReadProjectReferences(FixturePath("Sdk_NoRefs.csproj"));
        Assert.Empty(refs);
    }

    [Fact]
    public void ReadProjectReferences_ReturnsEmpty_WhenFileMalformed()
    {
        // Robustness: malformed csproj should not throw — caller treats it
        // as "no edges" and the project itself will fail to open later.
        var refs = ProjectGraphReader.ReadProjectReferences(FixturePath("Malformed.csproj"));
        Assert.Empty(refs);
    }
}
```

**Step 3: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ProjectGraphReaderTests" --nologo`
Expected: FAIL with `CS0117: 'ProjectGraphReader' does not contain a definition for 'ReadProjectReferences'` (or `CS0103`).

**Step 4: Implement**

`src/RoslynCodeLens/ProjectGraphReader.cs`:

```csharp
using System.Xml;

namespace RoslynCodeLens;

/// <summary>
/// Reads <c>&lt;ProjectReference Include="…"&gt;</c> targets from a single
/// <c>.csproj</c> via a lightweight XML pass. Does not perform MSBuild
/// evaluation, so it stays under 1ms per file even on large solutions.
/// Malformed files return an empty edge list — callers treat that as
/// "no edges"; the project itself will surface its parse failure later
/// when <c>MSBuildWorkspace</c> tries to open it.
/// </summary>
public static class ProjectGraphReader
{
    public static IReadOnlyList<string> ReadProjectReferences(string projectPath)
    {
        if (!File.Exists(projectPath)) return Array.Empty<string>();

        var dir = Path.GetDirectoryName(projectPath)!;
        var results = new List<string>();

        try
        {
            using var reader = XmlReader.Create(projectPath, new XmlReaderSettings { IgnoreComments = true });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (!string.Equals(reader.LocalName, "ProjectReference", StringComparison.Ordinal)) continue;

                var include = reader.GetAttribute("Include");
                if (string.IsNullOrWhiteSpace(include)) continue;

                var absolute = Path.GetFullPath(Path.Combine(dir, include));
                results.Add(absolute);
            }
        }
        catch (XmlException)
        {
            return Array.Empty<string>();
        }

        return results;
    }
}
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ProjectGraphReaderTests" --nologo`
Expected: PASS (3 tests).

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/ProjectGraphReader.cs tests/RoslynCodeLens.Tests/ProjectGraphReaderTests.cs tests/RoslynCodeLens.Tests/Fixtures/GraphReader/ tests/RoslynCodeLens.Tests/RoslynCodeLens.Tests.csproj
git commit -m "feat(loader): ProjectGraphReader — lightweight ProjectReference scan"
```

---

## Task 3: `ProjectClosure` — seed matching + BFS

**Files:**
- Create: `src/RoslynCodeLens/ProjectClosure.cs`
- Create: `tests/RoslynCodeLens.Tests/ProjectClosureTests.cs`

This is the pure-logic core. The closure operates over an **in-memory graph**, decoupled from filesystem — the integration with `ProjectGraphReader` happens in Task 5.

**Step 1: Write failing tests**

`tests/RoslynCodeLens.Tests/ProjectClosureTests.cs`:

```csharp
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class ProjectClosureTests
{
    // Helper: build a graph keyed by project name with name→referenced-names edges.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Graph(
        params (string From, string[] To)[] edges)
        => edges.ToDictionary(e => e.From, e => (IReadOnlyList<string>)e.To);

    private static IReadOnlyList<string> Names(params string[] names) => names;

    [Fact]
    public void Closure_FromGlobSeeds_IncludesTransitiveDeps()
    {
        var graph = Graph(
            ("App.Api",         new[] { "App.Domain" }),
            ("App.Domain",      new[] { "Shared.Common" }),
            ("Shared.Common",   Array.Empty<string>()),
            ("Sample.Unrelated", Array.Empty<string>()));

        var filter = new ProjectFilter(Include: new[] { "App.*" }, RootProjects: Array.Empty<string>());
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "App.Api", "App.Domain", "Shared.Common" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_FromRootProjects_IncludesTransitiveDeps()
    {
        var graph = Graph(
            ("App.Api",       new[] { "App.Domain" }),
            ("App.Domain",    new[] { "Shared.Common" }),
            ("Shared.Common", Array.Empty<string>()));

        var filter = new ProjectFilter(Include: Array.Empty<string>(), RootProjects: new[] { "App.Api" });
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "App.Api", "App.Domain", "Shared.Common" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_FromBoth_IsUnion()
    {
        var graph = Graph(
            ("App.Api",      new[] { "App.Domain" }),
            ("App.Domain",   Array.Empty<string>()),
            ("Tools.CLI",    Array.Empty<string>()),
            ("Sample.Other", Array.Empty<string>()));

        var filter = new ProjectFilter(Include: new[] { "Tools.*" }, RootProjects: new[] { "App.Api" });
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "App.Api", "App.Domain", "Tools.CLI" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_StopsAtCycles()
    {
        var graph = Graph(
            ("A", new[] { "B" }),
            ("B", new[] { "A" }));  // intentional cycle

        var filter = new ProjectFilter(Include: Array.Empty<string>(), RootProjects: new[] { "A" });
        var result = ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph);

        Assert.Equal(new[] { "A", "B" }, result.Loaded.OrderBy(x => x));
    }

    [Fact]
    public void Closure_EmptySeedSet_Throws()
    {
        var graph = Graph(("Lonely", Array.Empty<string>()));
        var filter = new ProjectFilter(Include: new[] { "DoesNotMatch.*" }, RootProjects: Array.Empty<string>());

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph));
        Assert.Contains("matched 0 projects", ex.Message);
        Assert.Contains("Lonely", ex.Message);
    }

    [Fact]
    public void Closure_UnknownRootProject_ThrowsListingMissing()
    {
        var graph = Graph(("App.Api", Array.Empty<string>()));
        var filter = new ProjectFilter(
            Include: Array.Empty<string>(),
            RootProjects: new[] { "App.Api", "App.Ghost", "App.Phantom" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph));
        Assert.Contains("App.Ghost", ex.Message);
        Assert.Contains("App.Phantom", ex.Message);
        Assert.DoesNotContain("App.Api,", ex.Message);   // App.Api exists; should not appear in missing list
    }

    [Fact]
    public void Closure_InvalidGlob_Throws()
    {
        var graph = Graph(("App.Api", Array.Empty<string>()));
        var filter = new ProjectFilter(Include: new[] { "App.[" }, RootProjects: Array.Empty<string>());

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProjectClosure.Compute(filter, allProjectNames: graph.Keys, graph));
        Assert.Contains("App.[", ex.Message);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ProjectClosureTests" --nologo`
Expected: FAIL with `CS0103: The name 'ProjectClosure' does not exist`.

**Step 3: Implement**

`src/RoslynCodeLens/ProjectClosure.cs`:

```csharp
using System.Text.RegularExpressions;

namespace RoslynCodeLens;

/// <summary>
/// Pure-logic closure walker. Given a <see cref="ProjectFilter"/>, the full
/// universe of project names, and a <c>name → referenced-names</c> graph,
/// returns the names that should be loaded.
///
/// <para>Seeds = (glob matches of <see cref="ProjectFilter.Include"/>)
/// ∪ (literal matches of <see cref="ProjectFilter.RootProjects"/>).
/// Loaded set = transitive BFS closure over the graph.</para>
/// </summary>
public static class ProjectClosure
{
    public sealed record Result(IReadOnlySet<string> Loaded);

    public static Result Compute(
        ProjectFilter filter,
        IEnumerable<string> allProjectNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var allSet = allProjectNames is HashSet<string> hs ? hs : new HashSet<string>(allProjectNames, StringComparer.Ordinal);

        // 1. Compile globs
        var includeRegexes = new List<Regex>(filter.Include.Count);
        foreach (var pattern in filter.Include)
        {
            try
            {
                includeRegexes.Add(GlobToRegex(pattern));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid include glob '{pattern}': {ex.Message}");
            }
        }

        // 2. Build seed set
        var seeds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in allSet)
        {
            foreach (var rx in includeRegexes)
            {
                if (rx.IsMatch(name)) { seeds.Add(name); break; }
            }
        }

        var missingRoots = new List<string>();
        foreach (var root in filter.RootProjects)
        {
            if (allSet.Contains(root)) seeds.Add(root);
            else missingRoots.Add(root);
        }

        if (missingRoots.Count > 0)
        {
            throw new InvalidOperationException(
                $"rootProjects names {missingRoots.Count} project(s) that do not exist in the solution: " +
                string.Join(", ", missingRoots) + ".");
        }

        if (seeds.Count == 0)
        {
            var available = string.Join(", ", allSet.OrderBy(n => n, StringComparer.Ordinal).Take(10));
            throw new InvalidOperationException(
                $"Filter matched 0 projects. Available project names (first 10): {available}. " +
                "Call list_solutions after loading without a filter to enumerate all.");
        }

        // 3. BFS
        var loaded = new HashSet<string>(seeds, StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!graph.TryGetValue(current, out var refs)) continue;
            foreach (var next in refs)
            {
                if (!allSet.Contains(next)) continue;  // dangling ref to a project not in the solution — skip
                if (loaded.Add(next)) queue.Enqueue(next);
            }
        }

        return new Result(loaded);
    }

    private static Regex GlobToRegex(string glob)
    {
        // Validate up-front so unbalanced `[` etc surface as ArgumentException.
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        // Trigger compilation now so invalid patterns throw here, not on first IsMatch.
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
```

Note: the glob-to-regex pass above only supports `*` and `?`. Brackets are not supported by `Regex.Escape`, which is what makes `"App.["` fail — but it fails at `IsMatch` time rather than construction. To make the `Closure_InvalidGlob_Throws` test pass, change the approach: validate `[…]` runs ourselves before escaping. Replace `GlobToRegex` with:

```csharp
private static Regex GlobToRegex(string glob)
{
    // Reject bracket characters explicitly — we don't support character classes,
    // and unescaped `[` would silently become a literal that never matches.
    if (glob.Contains('['))
        throw new ArgumentException("character classes '[…]' are not supported");

    var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ProjectClosureTests" --nologo`
Expected: PASS (7 tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/ProjectClosure.cs tests/RoslynCodeLens.Tests/ProjectClosureTests.cs
git commit -m "feat(loader): ProjectClosure — seed matching + BFS over project graph"
```

---

## Task 4: Create the `FilterableSolution` integration fixture

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/FilterableSolution/FilterableSolution.slnx`
- Create: 6 csproj files under sub-folders.

**Step 1: Create projects (no tests yet — fixture only)**

For each project, create `<Name>/<Name>.csproj` with the listed `ProjectReference` entries and a trivial source file so MSBuild has something to compile.

| Project | References | Source file |
|---|---|---|
| `App.Api` | `App.Domain`, `App.Infrastructure` | `Class1.cs` |
| `App.Domain` | `Shared.Common` | `Class1.cs` |
| `App.Infrastructure` | `App.Domain` | `Class1.cs` |
| `Shared.Common` | (none) | `Class1.cs` |
| `Sample.Unrelated` | (none) | `Class1.cs` |
| `App.Api.Tests` | `App.Api` | `Class1.cs` |

Template `csproj` (substitute the references):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\<RefName>\<RefName>.csproj" />
  </ItemGroup>
</Project>
```

Template `Class1.cs`:

```csharp
namespace <ProjectName>;

public class Class1 { }
```

`FilterableSolution.slnx`:

```xml
<Solution>
  <Project Path="App.Api/App.Api.csproj" />
  <Project Path="App.Domain/App.Domain.csproj" />
  <Project Path="App.Infrastructure/App.Infrastructure.csproj" />
  <Project Path="Shared.Common/Shared.Common.csproj" />
  <Project Path="Sample.Unrelated/Sample.Unrelated.csproj" />
  <Project Path="App.Api.Tests/App.Api.Tests.csproj" />
</Solution>
```

**Step 2: Wire fixture into test csproj copy-to-output**

Add to `tests/RoslynCodeLens.Tests/RoslynCodeLens.Tests.csproj`:

```xml
<ItemGroup>
  <None Include="Fixtures\FilterableSolution\**\*.csproj">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="Fixtures\FilterableSolution\**\*.slnx">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
  <None Include="Fixtures\FilterableSolution\**\*.cs">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

(Mirror the existing pattern used for `TestSolution` if it differs — search the csproj for `TestSolution` and adapt.)

**Step 3: Verify fixture builds standalone**

Run: `dotnet build tests/RoslynCodeLens.Tests/Fixtures/FilterableSolution/FilterableSolution.slnx --nologo`
Expected: success, all 6 projects compile.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/FilterableSolution/ tests/RoslynCodeLens.Tests/RoslynCodeLens.Tests.csproj
git commit -m "test(loader): FilterableSolution fixture for project-filter integration tests"
```

---

## Task 5: Wire filter into `SolutionLoader.OpenAsync`

**Files:**
- Modify: `src/RoslynCodeLens/SolutionLoader.cs` (signature + new filtered branch)
- Create: `tests/RoslynCodeLens.Tests/SolutionLoaderFilterTests.cs`

**Step 1: Write failing integration tests**

`tests/RoslynCodeLens.Tests/SolutionLoaderFilterTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class SolutionLoaderFilterTests
{
    private static string Slnx()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "FilterableSolution", "FilterableSolution.slnx");
        return Path.GetFullPath(path);
    }

    [Fact]
    public async Task OpenAsync_IncludeGlob_LoadsMatchingAndTransitive()
    {
        var loader = new SolutionLoader();
        var filter = new ProjectFilter(Include: new[] { "App.*" }, RootProjects: Array.Empty<string>());

        var (solution, workspace, skipped) = await loader.OpenAsync(Slnx(), filter);
        using var _ = workspace;

        var loadedNames = solution.Projects.Select(p => p.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "App.Api", "App.Api.Tests", "App.Domain", "App.Infrastructure", "Shared.Common" },
            loadedNames);
        Assert.Single(skipped, s => s.Name == "Sample.Unrelated");
    }

    [Fact]
    public async Task OpenAsync_RootProjects_LoadsClosure()
    {
        var loader = new SolutionLoader();
        var filter = new ProjectFilter(Include: Array.Empty<string>(), RootProjects: new[] { "App.Api" });

        var (solution, workspace, skipped) = await loader.OpenAsync(Slnx(), filter);
        using var _ = workspace;

        var loadedNames = solution.Projects.Select(p => p.Name).OrderBy(x => x).ToArray();
        Assert.Equal(
            new[] { "App.Api", "App.Domain", "App.Infrastructure", "Shared.Common" },
            loadedNames);
        Assert.Equal(2, skipped.Count);   // Sample.Unrelated + App.Api.Tests
    }

    [Fact]
    public async Task OpenAsync_NoFilter_BehavesAsBefore()
    {
        var loader = new SolutionLoader();
        var (solution, workspace, _) = await loader.OpenAsync(Slnx(), filter: null);
        using var _ws = workspace;

        Assert.Equal(6, solution.Projects.Count());
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~SolutionLoaderFilterTests" --nologo`
Expected: FAIL with `CS1501: No overload for method 'OpenAsync' takes 2 arguments` (or similar).

**Step 3: Add filter overload to `SolutionLoader.OpenAsync`**

Modify `src/RoslynCodeLens/SolutionLoader.cs`. The new signature accepts an optional `ProjectFilter`. When `filter is null || !filter.HasSeeds`, behaviour is unchanged. When the filter is active, take the per-project path with a closure-restricted project set.

```csharp
public async Task<(Solution Solution, MSBuildWorkspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenAsync(
    string solutionPath,
    ProjectFilter? filter = null,
    CancellationToken ct = default)
{
    var classified = ProjectClassifier.EnumerateProjects(solutionPath);

    if (filter is null || !filter.HasSeeds)
    {
        return await OpenUnfilteredAsync(solutionPath, classified, ct).ConfigureAwait(false);
    }

    return await OpenFilteredAsync(solutionPath, classified, filter, ct).ConfigureAwait(false);
}
```

Move the existing body of `OpenAsync` (everything after the `classified = ...` line) into a new private method `OpenUnfilteredAsync` with the same parameters: `string solutionPath, IReadOnlyList<ProjectClassifier.ClassifiedProject> classified, CancellationToken ct`.

Add the filtered branch:

```csharp
private async Task<(Solution Solution, MSBuildWorkspace Workspace, IReadOnlyList<SkippedProject> Skipped)> OpenFilteredAsync(
    string solutionPath,
    IReadOnlyList<ProjectClassifier.ClassifiedProject> classified,
    ProjectFilter filter,
    CancellationToken ct)
{
    // Build name -> referenced-names graph for SDK-style projects.
    var nameByPath = classified.ToDictionary(p => p.Path, p => p.Name, StringComparer.OrdinalIgnoreCase);
    var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    foreach (var entry in classified)
    {
        if (entry.Kind != ProjectClassifier.ProjectKind.SdkStyle) { graph[entry.Name] = Array.Empty<string>(); continue; }
        var refPaths = ProjectGraphReader.ReadProjectReferences(entry.Path);
        var refNames = new List<string>(refPaths.Count);
        foreach (var refPath in refPaths)
        {
            if (nameByPath.TryGetValue(refPath, out var refName)) refNames.Add(refName);
        }
        graph[entry.Name] = refNames;
    }

    var closure = ProjectClosure.Compute(filter, classified.Select(p => p.Name), graph);

    var workspace = MSBuildWorkspace.Create();
    workspace.WorkspaceFailed += (_, e) =>
        Console.Error.WriteLine($"[roslyn-codelens] Warning: {e.Diagnostic.Message}");

    await Console.Error.WriteLineAsync(
        $"[roslyn-codelens] Loading {closure.Loaded.Count}/{classified.Count} project(s) from {Path.GetFileName(solutionPath)} (filtered).")
        .ConfigureAwait(false);

    var skipped = new List<SkippedProject>();
    foreach (var entry in classified)
    {
        if (!closure.Loaded.Contains(entry.Name))
        {
            skipped.Add(new SkippedProject(entry.Path, entry.Name, "FilteredOut",
                "Excluded by load_solution filter."));
            continue;
        }

        if (entry.Kind != ProjectClassifier.ProjectKind.SdkStyle)
        {
            skipped.Add(new SkippedProject(entry.Path, entry.Name, entry.Kind.ToString(),
                entry.Reason ?? "Unsupported project format."));
            continue;
        }

        var timeoutSec = GetOpenProjectTimeoutSec();
        try
        {
            var loaded = await RunWithTimeoutAsync<Project>(
                innerCt => workspace.OpenProjectAsync(entry.Path, cancellationToken: innerCt),
                timeoutSec, ct).ConfigureAwait(false);

            if (loaded is null)
            {
                skipped.Add(new SkippedProject(entry.Path, entry.Name, "Timeout",
                    $"Project load exceeded {timeoutSec}s."));
            }
        }
        catch (Exception ex)
        {
            skipped.Add(new SkippedProject(entry.Path, entry.Name, "Failed",
                $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    return (workspace.CurrentSolution, workspace, skipped);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~SolutionLoaderFilterTests" --nologo`
Expected: PASS (3 tests).

**Step 5: Run the full test suite to make sure nothing else regressed**

Run: `dotnet test tests/RoslynCodeLens.Tests --nologo`
Expected: PASS (all existing tests still green; the existing `LoadSolution_ReturnsCompiledSolution` and friends still call the no-filter path).

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/SolutionLoader.cs tests/RoslynCodeLens.Tests/SolutionLoaderFilterTests.cs
git commit -m "feat(loader): SolutionLoader.OpenAsync accepts optional ProjectFilter"
```

---

## Task 6: Thread filter through `SolutionManager.CreateAsync`

**Files:**
- Modify: `src/RoslynCodeLens/SolutionManager.cs`

This is a pass-through change — no new tests, just plumbing.

**Step 1: Modify `CreateAsync`**

Search `src/RoslynCodeLens/SolutionManager.cs` for `CreateAsync(string solutionPath)` and add an optional `ProjectFilter? filter` parameter, then forward it to `loader.OpenAsync`:

```csharp
public static async Task<SolutionManager> CreateAsync(string solutionPath, ProjectFilter? filter = null)
{
    var loader = new SolutionLoader();
    Solution solution;
    IReadOnlyList<SkippedProject> skipped;
    try
    {
        (solution, _, skipped) = await loader.OpenAsync(solutionPath, filter).ConfigureAwait(false);
    }
    // ...rest unchanged...
}
```

**Step 2: Build to verify nothing breaks**

Run: `dotnet build src/RoslynCodeLens --nologo`
Expected: build success.

**Step 3: Run full test suite**

Run: `dotnet test tests/RoslynCodeLens.Tests --nologo`
Expected: PASS (nothing should regress; no callers pass the new parameter yet).

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/SolutionManager.cs
git commit -m "feat(loader): SolutionManager.CreateAsync forwards optional ProjectFilter"
```

---

## Task 7: Thread filter through `MultiSolutionManager.LoadSolutionAsync` (replace semantics)

**Files:**
- Modify: `src/RoslynCodeLens/MultiSolutionManager.cs`
- Create: `tests/RoslynCodeLens.Tests/MultiSolutionManagerFilterTests.cs`

**Step 1: Write failing replace-semantics test**

`tests/RoslynCodeLens.Tests/MultiSolutionManagerFilterTests.cs`:

```csharp
using RoslynCodeLens;

namespace RoslynCodeLens.Tests;

public class MultiSolutionManagerFilterTests
{
    private static string Slnx()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "FilterableSolution", "FilterableSolution.slnx");
        return Path.GetFullPath(path);
    }

    [Fact]
    public async Task LoadSolutionAsync_RepeatedCallWithDifferentFilter_ReplacesPreviousLoad()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        var slnx = Slnx();

        await manager.LoadSolutionAsync(slnx, new ProjectFilter(new[] { "App.*" }, Array.Empty<string>()));
        var firstActiveCount = manager.GetLoadedSolution().Solution.Projects.Count();

        await manager.LoadSolutionAsync(slnx, new ProjectFilter(Array.Empty<string>(), new[] { "Sample.Unrelated" }));
        var secondActiveCount = manager.GetLoadedSolution().Solution.Projects.Count();

        // First filter: App.* + transitive = 5 projects (App.Api/Domain/Infrastructure/Tests + Shared.Common).
        // Second filter: just Sample.Unrelated (no deps) = 1 project.
        Assert.Equal(5, firstActiveCount);
        Assert.Equal(1, secondActiveCount);
        Assert.Single(manager.ListSolutions());   // replace, not coexist
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~MultiSolutionManagerFilterTests" --nologo`
Expected: FAIL with `CS1501: No overload for method 'LoadSolutionAsync' takes 2 arguments`.

**Step 3: Implement**

In `src/RoslynCodeLens/MultiSolutionManager.cs`, modify `LoadSolutionAsync` to accept an optional `ProjectFilter? filter` and apply replace semantics when the path is already loaded:

```csharp
public async Task<string> LoadSolutionAsync(string solutionPath, ProjectFilter? filter = null)
{
    var normalised = Path.GetFullPath(solutionPath);

    SolutionManager? toDispose = null;
    lock (_lock)
    {
        if (_managers.TryGetValue(normalised, out var existing) && filter is not null && filter.HasSeeds)
        {
            // Replace semantics (Q4 in design): a filtered re-load disposes the previous workspace.
            _managers.Remove(normalised);
            toDispose = existing;
        }
        else if (_managers.ContainsKey(normalised) && (filter is null || !filter.HasSeeds))
        {
            // No-filter re-load of already-loaded path → existing fast path (re-activate).
            _activeKey = normalised;
            return normalised;
        }
    }
    toDispose?.Dispose();

    var manager = await SolutionManager.CreateAsync(normalised, filter).ConfigureAwait(false);

    if (manager.HasLoadFailure)
    {
        var message = manager.LoadFailureMessage!;
        manager.Dispose();
        throw new McpToolException(
            ToolErrorCode.InvalidArgument,
            message,
            new { solutionPath = normalised, reason = message });
    }

    lock (_lock)
    {
        if (_managers.ContainsKey(normalised)) { manager.Dispose(); }
        else { _managers[normalised] = manager; }
        _activeKey = normalised;
    }

    return normalised;
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~MultiSolutionManagerFilterTests" --nologo`
Expected: PASS.

**Step 5: Run full test suite**

Run: `dotnet test tests/RoslynCodeLens.Tests --nologo`
Expected: PASS (no regressions).

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/MultiSolutionManager.cs tests/RoslynCodeLens.Tests/MultiSolutionManagerFilterTests.cs
git commit -m "feat(loader): MultiSolutionManager.LoadSolutionAsync accepts ProjectFilter (replace semantics)"
```

---

## Task 8: Surface the filter on `LoadSolutionTool`

**Files:**
- Modify: `src/RoslynCodeLens/Tools/LoadSolutionTool.cs`
- Create: `tests/RoslynCodeLens.Tests/LoadSolutionToolTests.cs`

**Step 1: Write failing tool-layer tests**

`tests/RoslynCodeLens.Tests/LoadSolutionToolTests.cs`:

```csharp
using RoslynCodeLens;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class LoadSolutionToolTests
{
    private static string Slnx()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..",
            "Fixtures", "FilterableSolution", "FilterableSolution.slnx");
        return Path.GetFullPath(path);
    }

    [Fact]
    public async Task Execute_WithInclude_ReturnsLoadedAndSkippedCounts()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        var result = await LoadSolutionTool.Execute(manager, Slnx(),
            include: new[] { "App.*" }, rootProjects: null);

        Assert.Contains("Loaded 5", result);   // App.Api + Domain + Infrastructure + Tests + Shared.Common
        Assert.Contains("skipped 1", result);  // Sample.Unrelated
    }

    [Fact]
    public async Task Execute_NoFilter_BehavesAsBefore()
    {
        using var manager = MultiSolutionManager.CreateEmpty();
        var result = await LoadSolutionTool.Execute(manager, Slnx(),
            include: null, rootProjects: null);

        Assert.Contains("Loaded", result);
        Assert.DoesNotContain("skipped", result, StringComparison.OrdinalIgnoreCase);
        // Old single-line message must still work as a regression gate.
        Assert.StartsWith("Loaded", result.Trim());
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~LoadSolutionToolTests" --nologo`
Expected: FAIL with too-few-args compile error on the `Execute` overload.

**Step 3: Update `LoadSolutionTool`**

Replace `src/RoslynCodeLens/Tools/LoadSolutionTool.cs` with:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class LoadSolutionTool
{
    [McpServerTool(Name = "load_solution"),
     Description("Load a .sln/.slnx solution at runtime and make it the active solution. " +
                 "Pass `include` (glob array against project name) or `rootProjects` (exact names) to " +
                 "load only a subset; the loader walks ProjectReference transitively from those seeds " +
                 "to keep the workspace semantically complete. If the same `path` is already loaded, " +
                 "providing a new filter disposes the previous workspace (replace semantics). " +
                 "If the solution is already loaded with no filter, it simply activates it (~instant). " +
                 "New solutions take ~3 seconds to load and compile.")]
    public static async Task<string> Execute(
        MultiSolutionManager manager,
        [Description("Full path to the .sln or .slnx file to load")] string path,
        [Description("Optional glob patterns against project name (e.g. 'MyApp.*')")] string[]? include = null,
        [Description("Optional exact project names; both arrays act as seeds for a transitive ProjectReference closure")] string[]? rootProjects = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Solution file not found: {path}");

        ProjectFilter? filter = (include?.Length > 0 || rootProjects?.Length > 0)
            ? new ProjectFilter(include ?? Array.Empty<string>(), rootProjects ?? Array.Empty<string>())
            : null;

        var normalised = await manager.LoadSolutionAsync(path, filter).ConfigureAwait(false);
        var skipped = manager.GetActiveSkippedProjects();
        var loadedCount = manager.GetLoadedSolution().Solution.Projects.Count();

        if (skipped.Count == 0)
            return $"Loaded {loadedCount} project(s) from: {normalised}";

        var summary = string.Join(", ", skipped.Take(10).Select(s => $"{s.Name} ({s.Kind})"));
        var ellipsis = skipped.Count > 10 ? $" and {skipped.Count - 10} more" : "";
        return $"Loaded {loadedCount} project(s) from: {normalised}; skipped {skipped.Count}: {summary}{ellipsis}. " +
               "Call list_solutions for per-project reason details.";
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~LoadSolutionToolTests" --nologo`
Expected: PASS (2 tests).

**Step 5: Run full test suite**

Run: `dotnet test tests/RoslynCodeLens.Tests --nologo`
Expected: PASS (all tests green).

**Step 6: Commit**

```bash
git add src/RoslynCodeLens/Tools/LoadSolutionTool.cs tests/RoslynCodeLens.Tests/LoadSolutionToolTests.cs
git commit -m "feat(tools): expose include/rootProjects filter on load_solution"
```

---

## Task 9: Open the PR

**Step 1: Push the branch**

Run: `git push -u origin feat/load-solution-project-filter`
Expected: branch pushed.

**Step 2: Open the PR**

Run:

```bash
gh pr create \
  --base main \
  --title "feat(loader): project-filter for load_solution (#232)" \
  --body "$(cat <<'EOF'
Implements the design in [docs/plans/2026-06-17-project-filter-load-solution-design.md](docs/plans/2026-06-17-project-filter-load-solution-design.md).

Adds optional `include` (glob array) and `rootProjects` (exact-name array) parameters to `load_solution`. Both act as seeds for a transitive `ProjectReference` closure, so the resulting workspace is semantically complete. No-filter behaviour unchanged (backward compatible).

Closes #232.

Companion items (parallel per-project loader, async load_solution with a load handle) remain deferred to BACKLOG section 4.
EOF
)"
```

Expected: PR URL returned.

---

## Done

After PR merges, update the in-flight note in `docs/BACKLOG.md` if it points to this work, and reply to issue #232 with the PR link.
