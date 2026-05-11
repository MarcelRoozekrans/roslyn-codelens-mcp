# Trust Gate and Analyzer Allowlist Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Mitigate [GHSA-552p-8f74-6x7q](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/security/advisories/GHSA-552p-8f74-6x7q) by adding a VS/Rider-style trust model for solutions and a path-based allowlist for analyzer DLLs, so opening an attacker-controlled `.sln` no longer auto-executes attacker code.

**Architecture:** Two collaborating services injected as singletons. `TrustStore` is the persistence layer (`%APPDATA%\roslyn-codelens\trust.json` + in-memory session entries) that records which solution paths the user has trusted. `AnalyzerAllowlist` is a pure path classifier that decides whether each analyzer DLL path falls inside an acceptable boundary (NuGet global packages, dotnet SDK install dir, solution `bin`/`obj`). `AnalyzerRunner.GetAnalyzers` consults the allowlist; `GetDiagnosticsLogic` consults the trust store before requesting analyzers. New MCP tools — `trust_solution`, `list_trusted_paths`, `revoke_trust` — let the AI mutate trust state, with the Claude Code permission prompt acting as the human checkpoint analogous to VS's "Trust this folder?" dialog. `includeAnalyzers` default flips to `false` as belt-and-suspenders.

**Tech Stack:** .NET 10, Roslyn (`Microsoft.CodeAnalysis`), `Microsoft.Extensions.DependencyInjection`, ModelContextProtocol SDK, `System.Text.Json`, xUnit.

**Decided design points (resolved up front so the implementer doesn't need to):**

1. **First-run behavior when `trust.json` doesn't exist:** auto-trust the solution path passed on the CLI to the server at startup, as a *session-scoped* entry (in-memory only). The user already deliberately pointed the server at this solution; treating that as one implicit trust grant matches user intent. The session trust is *not* persisted; explicit `trust_solution` call is still required for `persistent` scope.
2. **Trust scopes on `trust_solution`:** `session` (in-memory, default), `persistent` (writes to `trust.json`), `addRoot` (persistent, adds a trusted-root prefix instead of an exact path). No `once` scope — overcomplicates the API; "session" is good enough.
3. **Hash pinning of analyzer DLLs:** **deferred to a future advisory follow-up**. The path allowlist alone closes the auto-load attack chain; hash pinning is a defense-in-depth improvement that adds material complexity (filewatcher invalidation, perf cost on every `get_diagnostics`).
4. **Analyzer policy default:** `nuget-and-solution-bin`. This permits paths under `<userprofile>\.nuget\packages\`, the dotnet SDK install root (resolved via `MSBuildLocator`), and `<solution-dir>\**\bin\**` / `<solution-dir>\**\obj\**`. Solution-local analyzer DLLs outside `bin/obj` (e.g., `tools/EvilAnalyzer.dll` checked into the repo) are skipped. This is stricter than VS, but justified by the AI-auto-invocation threat model.
5. **What happens to compiler diagnostics when solution is untrusted?** Still returned. Only analyzer diagnostics are gated. The user shouldn't lose the basic "are there compile errors?" capability over trust state.

---

## Task 1: TrustStore model + JSON shape

**Files:**
- Create: `src/RoslynCodeLens/Security/TrustStoreModel.cs`
- Create: `tests/RoslynCodeLens.Tests/Security/TrustStoreModelTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/RoslynCodeLens.Tests/Security/TrustStoreModelTests.cs
using System.Text.Json;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tests.Security;

public class TrustStoreModelTests
{
    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var model = new TrustStoreModel
        {
            Version = 1,
            TrustedRoots = ["c:\\projects\\", "c:\\work\\"],
            TrustedSolutions =
            [
                new TrustedSolution("c:\\repos\\foo.sln", DateTimeOffset.Parse("2026-05-11T10:00:00Z"))
            ],
            AnalyzerPolicy = "nuget-and-solution-bin"
        };

        var json = JsonSerializer.Serialize(model, TrustStoreModel.JsonOptions);
        var roundtripped = JsonSerializer.Deserialize<TrustStoreModel>(json, TrustStoreModel.JsonOptions);

        Assert.NotNull(roundtripped);
        Assert.Equal(1, roundtripped.Version);
        Assert.Equal(["c:\\projects\\", "c:\\work\\"], roundtripped.TrustedRoots);
        Assert.Single(roundtripped.TrustedSolutions);
        Assert.Equal("c:\\repos\\foo.sln", roundtripped.TrustedSolutions[0].Path);
        Assert.Equal("nuget-and-solution-bin", roundtripped.AnalyzerPolicy);
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var model = new TrustStoreModel();
        Assert.Equal(1, model.Version);
        Assert.Empty(model.TrustedRoots);
        Assert.Empty(model.TrustedSolutions);
        Assert.Equal("nuget-and-solution-bin", model.AnalyzerPolicy);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~TrustStoreModelTests"`
Expected: FAIL — `TrustStoreModel` type does not exist.

**Step 3: Write minimal implementation**

```csharp
// src/RoslynCodeLens/Security/TrustStoreModel.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynCodeLens.Security;

public sealed class TrustStoreModel
{
    public int Version { get; set; } = 1;
    public List<string> TrustedRoots { get; set; } = [];
    public List<TrustedSolution> TrustedSolutions { get; set; } = [];
    public string AnalyzerPolicy { get; set; } = "nuget-and-solution-bin";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record TrustedSolution(string Path, DateTimeOffset AddedUtc);
```

**Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~TrustStoreModelTests"`
Expected: PASS (2 tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Security/TrustStoreModel.cs tests/RoslynCodeLens.Tests/Security/TrustStoreModelTests.cs
git commit -m "feat(security): add TrustStoreModel for persisted trust state"
```

---

## Task 2: TrustStore service (load/save + in-memory session entries)

**Files:**
- Create: `src/RoslynCodeLens/Security/TrustStore.cs`
- Create: `tests/RoslynCodeLens.Tests/Security/TrustStoreTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/RoslynCodeLens.Tests/Security/TrustStoreTests.cs
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tests.Security;

public class TrustStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _trustFile;

    public TrustStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trust-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _trustFile = Path.Combine(_tempDir, "trust.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void IsTrusted_EmptyStore_ReturnsFalse()
    {
        var store = new TrustStore(_trustFile);
        Assert.False(store.IsTrusted("c:\\repos\\foo.sln"));
    }

    [Fact]
    public void IsTrusted_AfterAddSession_ReturnsTrue_ButFileNotCreated()
    {
        var store = new TrustStore(_trustFile);
        store.AddSessionTrust("c:\\repos\\foo.sln");

        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));
        Assert.False(File.Exists(_trustFile));
    }

    [Fact]
    public void IsTrusted_AfterAddPersistent_ReturnsTrue_AndFileWritten()
    {
        var store = new TrustStore(_trustFile);
        store.AddPersistentTrust("c:\\repos\\foo.sln");

        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));
        Assert.True(File.Exists(_trustFile));

        var reloaded = new TrustStore(_trustFile);
        Assert.True(reloaded.IsTrusted("c:\\repos\\foo.sln"));
    }

    [Fact]
    public void IsTrusted_PathUnderTrustedRoot_ReturnsTrue()
    {
        var store = new TrustStore(_trustFile);
        store.AddTrustedRoot("c:\\projects\\");

        Assert.True(store.IsTrusted("c:\\projects\\repo\\foo.sln"));
        Assert.True(store.IsTrusted("c:\\projects\\nested\\dir\\bar.sln"));
        Assert.False(store.IsTrusted("c:\\other\\foo.sln"));
    }

    [Fact]
    public void Revoke_RemovesPersistentEntry()
    {
        var store = new TrustStore(_trustFile);
        store.AddPersistentTrust("c:\\repos\\foo.sln");
        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));

        store.Revoke("c:\\repos\\foo.sln");
        Assert.False(store.IsTrusted("c:\\repos\\foo.sln"));
    }

    [Fact]
    public void Revoke_RemovesSessionEntry()
    {
        var store = new TrustStore(_trustFile);
        store.AddSessionTrust("c:\\repos\\foo.sln");
        store.Revoke("c:\\repos\\foo.sln");
        Assert.False(store.IsTrusted("c:\\repos\\foo.sln"));
    }

    [Fact]
    public void PathComparison_IsCaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new TrustStore(_trustFile);
        store.AddSessionTrust("c:\\Repos\\Foo.sln");
        Assert.True(store.IsTrusted("C:\\REPOS\\foo.SLN"));
    }

    [Fact]
    public void List_ReturnsAllEntries()
    {
        var store = new TrustStore(_trustFile);
        store.AddSessionTrust("c:\\repos\\a.sln");
        store.AddPersistentTrust("c:\\repos\\b.sln");
        store.AddTrustedRoot("c:\\projects\\");

        var snapshot = store.GetSnapshot();
        Assert.Contains(snapshot.SessionSolutions, s => s.Equals("c:\\repos\\a.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.PersistentSolutions, s => s.Path.Equals("c:\\repos\\b.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(snapshot.TrustedRoots, r => r.Equals("c:\\projects\\", StringComparison.OrdinalIgnoreCase));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TrustStoreTests"`
Expected: FAIL — `TrustStore` type does not exist.

**Step 3: Write minimal implementation**

```csharp
// src/RoslynCodeLens/Security/TrustStore.cs
using System.Text.Json;

namespace RoslynCodeLens.Security;

public sealed class TrustStore
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly HashSet<string> _sessionSolutions;
    private TrustStoreModel _persistent;

    public TrustStore(string filePath)
    {
        _filePath = filePath;
        _sessionSolutions = new HashSet<string>(PathComparer);
        _persistent = LoadFromDisk();
    }

    public static string DefaultFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "roslyn-codelens", "trust.json");

    public bool IsTrusted(string solutionPath)
    {
        var normalized = Normalize(solutionPath);
        lock (_lock)
        {
            if (_sessionSolutions.Contains(normalized)) return true;
            if (_persistent.TrustedSolutions.Any(t => Normalize(t.Path).Equals(normalized, PathComparison))) return true;
            foreach (var root in _persistent.TrustedRoots)
            {
                var normRoot = Normalize(root);
                if (normalized.StartsWith(normRoot, PathComparison)) return true;
            }
            return false;
        }
    }

    public void AddSessionTrust(string solutionPath)
    {
        lock (_lock) _sessionSolutions.Add(Normalize(solutionPath));
    }

    public void AddPersistentTrust(string solutionPath)
    {
        lock (_lock)
        {
            var norm = Normalize(solutionPath);
            if (!_persistent.TrustedSolutions.Any(t => Normalize(t.Path).Equals(norm, PathComparison)))
                _persistent.TrustedSolutions.Add(new TrustedSolution(solutionPath, DateTimeOffset.UtcNow));
            SaveToDisk();
        }
    }

    public void AddTrustedRoot(string rootPath)
    {
        lock (_lock)
        {
            var norm = Normalize(rootPath);
            if (!_persistent.TrustedRoots.Any(r => Normalize(r).Equals(norm, PathComparison)))
                _persistent.TrustedRoots.Add(rootPath);
            SaveToDisk();
        }
    }

    public void Revoke(string solutionPath)
    {
        lock (_lock)
        {
            var norm = Normalize(solutionPath);
            _sessionSolutions.Remove(norm);
            _persistent.TrustedSolutions.RemoveAll(t => Normalize(t.Path).Equals(norm, PathComparison));
            _persistent.TrustedRoots.RemoveAll(r => Normalize(r).Equals(norm, PathComparison));
            SaveToDisk();
        }
    }

    public string AnalyzerPolicy
    {
        get { lock (_lock) return _persistent.AnalyzerPolicy; }
    }

    public TrustSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new TrustSnapshot(
                _sessionSolutions.ToList(),
                _persistent.TrustedSolutions.ToList(),
                _persistent.TrustedRoots.ToList(),
                _persistent.AnalyzerPolicy);
        }
    }

    private TrustStoreModel LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return new TrustStoreModel();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<TrustStoreModel>(json, TrustStoreModel.JsonOptions) ?? new TrustStoreModel();
        }
        catch
        {
            return new TrustStoreModel();
        }
    }

    private void SaveToDisk()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_persistent, TrustStoreModel.JsonOptions));
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static IEqualityComparer<string> PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

public sealed record TrustSnapshot(
    IReadOnlyList<string> SessionSolutions,
    IReadOnlyList<TrustedSolution> PersistentSolutions,
    IReadOnlyList<string> TrustedRoots,
    string AnalyzerPolicy);
```

**Step 4: Run tests to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~TrustStoreTests"`
Expected: PASS (8 tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Security/TrustStore.cs tests/RoslynCodeLens.Tests/Security/TrustStoreTests.cs
git commit -m "feat(security): add TrustStore with session+persistent trust"
```

---

## Task 3: AnalyzerAllowlist policy classifier

**Files:**
- Create: `src/RoslynCodeLens/Security/AnalyzerAllowlist.cs`
- Create: `tests/RoslynCodeLens.Tests/Security/AnalyzerAllowlistTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/RoslynCodeLens.Tests/Security/AnalyzerAllowlistTests.cs
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tests.Security;

public class AnalyzerAllowlistTests
{
    private static readonly string Nuget = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    [Fact]
    public void NugetGlobalPackages_IsAllowed_UnderDefaultPolicy()
    {
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
        var dll = Path.Combine(Nuget, "stylecop.analyzers", "1.2.0", "analyzers", "dotnet", "cs", "StyleCop.Analyzers.dll");
        Assert.True(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void SolutionBinPath_IsAllowed_UnderDefaultPolicy()
    {
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
        var dll = Path.Combine("c:\\repos\\my-app", "src", "MyProj", "bin", "Debug", "net8.0", "MyProj.Analyzers.dll");
        Assert.True(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void RandomPath_IsRejected_UnderDefaultPolicy()
    {
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
        Assert.False(policy.IsAllowed("c:\\evil\\Analyzer.dll", solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void SolutionLocalToolsPath_IsRejected_UnderDefaultPolicy()
    {
        // tools/EvilAnalyzer.dll checked into a "trusted" repo is still rejected
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
        var dll = Path.Combine("c:\\repos\\my-app", "tools", "EvilAnalyzer.dll");
        Assert.False(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void DotnetSdkPath_IsAllowed_WhenSdkRootKnown()
    {
        var sdkRoot = OperatingSystem.IsWindows() ? "c:\\Program Files\\dotnet" : "/usr/share/dotnet";
        var policy = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: sdkRoot);
        var dll = Path.Combine(sdkRoot, "sdk", "10.0.100", "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll");
        Assert.True(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void AllPolicy_AllowsEverything()
    {
        var policy = new AnalyzerAllowlist("all", dotnetSdkRoot: null);
        Assert.True(policy.IsAllowed("c:\\evil\\Analyzer.dll", solutionDir: "c:\\repos\\my-app"));
    }

    [Fact]
    public void StrictPolicy_RejectsSolutionBin()
    {
        var policy = new AnalyzerAllowlist("strict", dotnetSdkRoot: null);
        var dll = Path.Combine("c:\\repos\\my-app", "src", "MyProj", "bin", "Debug", "net8.0", "MyProj.Analyzers.dll");
        Assert.False(policy.IsAllowed(dll, solutionDir: "c:\\repos\\my-app"));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AnalyzerAllowlistTests"`
Expected: FAIL — `AnalyzerAllowlist` does not exist.

**Step 3: Write minimal implementation**

```csharp
// src/RoslynCodeLens/Security/AnalyzerAllowlist.cs
namespace RoslynCodeLens.Security;

public sealed class AnalyzerAllowlist
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _policy;
    private readonly string _nugetGlobal;
    private readonly string? _dotnetSdkRoot;

    public AnalyzerAllowlist(string policy, string? dotnetSdkRoot)
    {
        _policy = policy;
        _nugetGlobal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");
        _dotnetSdkRoot = dotnetSdkRoot;
    }

    public bool IsAllowed(string analyzerDllPath, string solutionDir)
    {
        if (string.Equals(_policy, "all", StringComparison.OrdinalIgnoreCase))
            return true;

        var full = Path.GetFullPath(analyzerDllPath);

        if (StartsWith(full, _nugetGlobal)) return true;
        if (_dotnetSdkRoot is not null && StartsWith(full, _dotnetSdkRoot)) return true;

        if (string.Equals(_policy, "strict", StringComparison.OrdinalIgnoreCase))
            return false;

        // nuget-and-solution-bin (default): also accept solution-local bin/obj
        var binRoot = Path.GetFullPath(solutionDir);
        if (StartsWith(full, binRoot))
        {
            var rel = full.Substring(binRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var segments = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(s => string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static bool StartsWith(string path, string prefix)
    {
        var p = Path.GetFullPath(prefix).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        var full = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        return path.StartsWith(p, PathComparison) || full.StartsWith(p, PathComparison);
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~AnalyzerAllowlistTests"`
Expected: PASS (7 tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Security/AnalyzerAllowlist.cs tests/RoslynCodeLens.Tests/Security/AnalyzerAllowlistTests.cs
git commit -m "feat(security): add AnalyzerAllowlist policy classifier"
```

---

## Task 4: Thread allowlist into AnalyzerRunner

**Files:**
- Modify: `src/RoslynCodeLens/AnalyzerRunner.cs`
- Modify: `tests/RoslynCodeLens.Tests/AnalyzerRunnerTests.cs`

**Step 1: Add failing test that proves rejected DLLs are skipped**

Append to `tests/RoslynCodeLens.Tests/AnalyzerRunnerTests.cs`:

```csharp
[Fact]
public async Task RunAnalyzersAsync_WithStrictAllowlist_SkipsAllSolutionLocalAnalyzers()
{
    var project = _loaded.Solution.Projects.First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal));
    var compilation = _loaded.Compilations[project.Id];

    // strict policy rejects everything outside NuGet / SDK; if the test fixture's
    // analyzers come from NuGet (they do — CA*), this still runs them. So we
    // instead use an "impossible-prefix" allowlist via a custom policy stub.
    var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("strict", dotnetSdkRoot: "/nonexistent");
    // Override NuGet path via env so nothing matches:
    var prev = Environment.GetEnvironmentVariable("USERPROFILE");
    try
    {
        Environment.SetEnvironmentVariable("USERPROFILE", "/nonexistent-nuget-root");
        var allowlistEmpty = new RoslynCodeLens.Security.AnalyzerAllowlist("strict", dotnetSdkRoot: "/nonexistent");
        var diagnostics = await AnalyzerRunner.RunAnalyzersAsync(project, compilation, allowlistEmpty, CancellationToken.None);
        // All analyzers should have been filtered → no analyzer-sourced diagnostics
        Assert.Empty(diagnostics);
    }
    finally
    {
        Environment.SetEnvironmentVariable("USERPROFILE", prev);
    }
}
```

Also update the existing two tests to pass a permissive allowlist:

```csharp
// Replace existing calls to AnalyzerRunner.RunAnalyzersAsync(project, compilation, CancellationToken.None)
// with the new 4-argument form passing a permissive allowlist:
var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("all", dotnetSdkRoot: null);
var diagnostics = await AnalyzerRunner.RunAnalyzersAsync(project, compilation, allowlist, CancellationToken.None);
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AnalyzerRunnerTests"`
Expected: FAIL — `RunAnalyzersAsync` does not take an allowlist parameter.

**Step 3: Modify AnalyzerRunner**

Replace `src/RoslynCodeLens/AnalyzerRunner.cs` with:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynCodeLens.Security;

namespace RoslynCodeLens;

public static class AnalyzerRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<ImmutableArray<Diagnostic>> RunAnalyzersAsync(
        Project project,
        Compilation compilation,
        AnalyzerAllowlist allowlist,
        CancellationToken ct)
    {
        var analyzers = GetAnalyzers(project, allowlist);
        if (analyzers.IsEmpty)
            return ImmutableArray<Diagnostic>.Empty;

        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, options: null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            var results = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(timeoutCts.Token).ConfigureAwait(false);
            return results;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ImmutableArray<Diagnostic>.Empty;
        }
    }

    private static ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(Project project, AnalyzerAllowlist allowlist)
    {
        var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var solutionDir = Path.GetDirectoryName(project.Solution.FilePath) ?? "";

        foreach (var analyzerRef in project.AnalyzerReferences)
        {
            // FullPath is null for in-memory analyzer references; treat those as not-allowed
            // unless policy is "all" (which the allowlist enforces internally).
            var path = analyzerRef.FullPath;
            if (path is null || !allowlist.IsAllowed(path, solutionDir))
                continue;

            foreach (var analyzer in analyzerRef.GetAnalyzers(project.Language))
                analyzers.Add(analyzer);
        }

        return analyzers.ToImmutable();
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~AnalyzerRunnerTests"`
Expected: PASS (3 tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/AnalyzerRunner.cs tests/RoslynCodeLens.Tests/AnalyzerRunnerTests.cs
git commit -m "feat(security): filter analyzer DLLs through AnalyzerAllowlist"
```

---

## Task 5: Thread trust + allowlist into GetDiagnosticsLogic AND GetCodeFixesLogic

**Note:** Originally scoped to `GetDiagnosticsLogic` only. Expanded during implementation because `GetCodeFixesLogic` is a second, undocumented caller of `AnalyzerRunner.RunAnalyzersAsync` that runs analyzers unconditionally — the same code-execution primitive without any trust gate. `CodeFixRunnerTests.cs` also calls the old signature directly.

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetDiagnosticsLogic.cs`
- Modify: `src/RoslynCodeLens/Tools/GetCodeFixesLogic.cs`
- Modify: `tests/RoslynCodeLens.Tests/Tools/GetDiagnosticsToolTests.cs`
- Modify: `tests/RoslynCodeLens.Tests/CodeFixRunnerTests.cs`

**Step 1: Add failing tests**

Append to `tests/RoslynCodeLens.Tests/Tools/GetDiagnosticsToolTests.cs`:

```csharp
[Fact]
public async Task GetDiagnostics_UntrustedSolution_ThrowsWhenAnalyzersRequested()
{
    using var tempFile = new TempTrustFile();
    var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
    var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
        await GetDiagnosticsLogic.ExecuteAsync(
            _loaded, _resolver, null, null, includeAnalyzers: true,
            trustStore, allowlist, CancellationToken.None);
    });
    Assert.Contains("not trusted", ex.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("trust_solution", ex.Message, StringComparison.Ordinal);
}

[Fact]
public async Task GetDiagnostics_UntrustedSolution_StillReturnsCompilerDiagnosticsWithoutAnalyzers()
{
    using var tempFile = new TempTrustFile();
    var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
    var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);

    var results = await GetDiagnosticsLogic.ExecuteAsync(
        _loaded, _resolver, null, null, includeAnalyzers: false,
        trustStore, allowlist, CancellationToken.None);

    Assert.All(results, d => Assert.Equal("compiler", d.Source));
}

[Fact]
public async Task GetDiagnostics_TrustedSolution_RunsAnalyzers()
{
    using var tempFile = new TempTrustFile();
    var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
    trustStore.AddSessionTrust(_loaded.Solution.FilePath!);
    var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);

    var results = await GetDiagnosticsLogic.ExecuteAsync(
        _loaded, _resolver, null, null, includeAnalyzers: true,
        trustStore, allowlist, CancellationToken.None);

    Assert.Contains(results, d => d.Source.StartsWith("analyzer:", StringComparison.Ordinal));
}

private sealed class TempTrustFile : IDisposable
{
    public string Path { get; }
    public TempTrustFile()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json");
    }
    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}
```

Also update the existing analyzer-related tests to pass the new arguments:

```csharp
// Replace existing GetDiagnosticsLogic.ExecuteAsync(_loaded, _resolver, null, null, includeAnalyzers: true)
// with:
using var tempFile = new TempTrustFile();
var trustStore = new RoslynCodeLens.Security.TrustStore(tempFile.Path);
trustStore.AddSessionTrust(_loaded.Solution.FilePath!);
var allowlist = new RoslynCodeLens.Security.AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
var results = await GetDiagnosticsLogic.ExecuteAsync(
    _loaded, _resolver, null, null, includeAnalyzers: true,
    trustStore, allowlist, CancellationToken.None);
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~GetDiagnosticsToolTests"`
Expected: FAIL — `ExecuteAsync` signature does not match.

**Step 3: Modify GetDiagnosticsLogic**

Replace the `ExecuteAsync` method in `src/RoslynCodeLens/Tools/GetDiagnosticsLogic.cs`:

```csharp
public static async Task<IReadOnlyList<DiagnosticInfo>> ExecuteAsync(
    LoadedSolution loaded,
    SymbolResolver resolver,
    string? project,
    string? severity,
    bool includeAnalyzers,
    Security.TrustStore trustStore,
    Security.AnalyzerAllowlist allowlist,
    CancellationToken ct = default)
{
    var results = CollectCompilerDiagnostics(loaded, resolver, project, severity);

    if (!includeAnalyzers)
        return results;

    var solutionPath = loaded.Solution.FilePath;
    if (solutionPath is null || !trustStore.IsTrusted(solutionPath))
    {
        throw new InvalidOperationException(
            $"Solution '{solutionPath ?? "<unknown>"}' is not trusted for analyzer execution. " +
            $"Analyzer DLLs run as in-process code, so the user must explicitly authorize them. " +
            $"Ask the user, then call the 'trust_solution' tool with this path. " +
            $"To get compiler-only diagnostics, retry with includeAnalyzers=false.");
    }

    var minSeverity = ParseMinSeverity(severity);

    foreach (var (projectId, compilation) in loaded.Compilations)
    {
        var projectName = resolver.GetProjectName(projectId);

        if (project != null &&
            !projectName.Contains(project, StringComparison.OrdinalIgnoreCase))
            continue;

        var roslynProject = loaded.Solution.GetProject(projectId);
        if (roslynProject == null)
            continue;

        var analyzerDiagnostics = await AnalyzerRunner.RunAnalyzersAsync(
            roslynProject, compilation, allowlist, ct).ConfigureAwait(false);

        foreach (var diagnostic in analyzerDiagnostics)
        {
            if (diagnostic.Severity < minSeverity)
                continue;

            var lineSpan = diagnostic.Location.GetLineSpan();
            var file = lineSpan.Path ?? "";
            var line = lineSpan.StartLinePosition.Line + 1;

            results.Add(new DiagnosticInfo(
                diagnostic.Id,
                diagnostic.Severity.ToString(),
                diagnostic.GetMessage(),
                file,
                line,
                projectName,
                $"analyzer:{diagnostic.Id}"));
        }
    }

    return results;
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~GetDiagnosticsToolTests"`
Expected: PASS (all tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetDiagnosticsLogic.cs tests/RoslynCodeLens.Tests/Tools/GetDiagnosticsToolTests.cs
git commit -m "feat(security): gate analyzer execution behind TrustStore in GetDiagnosticsLogic"
```

---

## Task 6: Flip `includeAnalyzers` default + wire DI in GetDiagnosticsTool

**Files:**
- Modify: `src/RoslynCodeLens/Tools/GetDiagnosticsTool.cs`

**Step 1: Write the failing test**

Append to `tests/RoslynCodeLens.Tests/Tools/GetDiagnosticsToolTests.cs`:

```csharp
[Fact]
public void GetDiagnosticsTool_IncludeAnalyzers_DefaultsToFalse()
{
    var method = typeof(GetDiagnosticsTool).GetMethod(nameof(GetDiagnosticsTool.Execute))!;
    var param = method.GetParameters().Single(p => p.Name == "includeAnalyzers");
    Assert.True(param.HasDefaultValue);
    Assert.Equal(false, param.DefaultValue);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "GetDiagnosticsTool_IncludeAnalyzers_DefaultsToFalse"`
Expected: FAIL — default is `true`.

**Step 3: Modify GetDiagnosticsTool**

Replace `src/RoslynCodeLens/Tools/GetDiagnosticsTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetDiagnosticsTool
{
    [McpServerTool(Name = "get_diagnostics"),
     Description("List compiler errors and warnings across the solution, optionally including analyzer diagnostics. " +
                 "Analyzer diagnostics require the solution to be trusted (see 'trust_solution').")]
    public static async Task<IReadOnlyList<DiagnosticInfo>> Execute(
        MultiSolutionManager manager,
        TrustStore trustStore,
        AnalyzerAllowlist allowlist,
        [Description("Optional project name filter")] string? project = null,
        [Description("Minimum severity: 'error' or 'warning' (default: warning)")] string? severity = null,
        [Description("Include analyzer diagnostics (default: false — requires trust_solution to be called first)")]
            bool includeAnalyzers = false,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        return await GetDiagnosticsLogic.ExecuteAsync(
            manager.GetLoadedSolution(), manager.GetResolver(),
            project, severity, includeAnalyzers,
            trustStore, allowlist, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Run test to verify pass**

Run: `dotnet test --filter "GetDiagnosticsTool_IncludeAnalyzers_DefaultsToFalse"`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/GetDiagnosticsTool.cs tests/RoslynCodeLens.Tests/Tools/GetDiagnosticsToolTests.cs
git commit -m "feat(security): default get_diagnostics includeAnalyzers=false; require trust"
```

---

## Task 7: `trust_solution` MCP tool

**Files:**
- Create: `src/RoslynCodeLens/Tools/TrustSolutionTool.cs`
- Create: `src/RoslynCodeLens/Tools/TrustSolutionLogic.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/TrustSolutionLogicTests.cs`

**Step 1: Write the failing tests**

```csharp
// tests/RoslynCodeLens.Tests/Tools/TrustSolutionLogicTests.cs
using RoslynCodeLens.Security;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class TrustSolutionLogicTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void SessionScope_AddsSessionTrust_NoFileWritten()
    {
        var store = new TrustStore(_tempFile);
        var result = TrustSolutionLogic.Execute(store, "c:\\repos\\foo.sln", "session");
        Assert.Equal("session", result.Scope);
        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));
        Assert.False(File.Exists(_tempFile));
    }

    [Fact]
    public void PersistentScope_AddsPersistentTrust_FileWritten()
    {
        var store = new TrustStore(_tempFile);
        var result = TrustSolutionLogic.Execute(store, "c:\\repos\\foo.sln", "persistent");
        Assert.Equal("persistent", result.Scope);
        Assert.True(File.Exists(_tempFile));
    }

    [Fact]
    public void AddRootScope_AddsTrustedRoot()
    {
        var store = new TrustStore(_tempFile);
        var result = TrustSolutionLogic.Execute(store, "c:\\projects\\", "addRoot");
        Assert.Equal("addRoot", result.Scope);
        Assert.True(store.IsTrusted("c:\\projects\\anyrepo\\foo.sln"));
    }

    [Fact]
    public void InvalidScope_Throws()
    {
        var store = new TrustStore(_tempFile);
        Assert.Throws<ArgumentException>(() =>
            TrustSolutionLogic.Execute(store, "c:\\repos\\foo.sln", "lifetime"));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TrustSolutionLogicTests"`
Expected: FAIL — types do not exist.

**Step 3: Write minimal implementations**

```csharp
// src/RoslynCodeLens/Tools/TrustSolutionLogic.cs
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

public static class TrustSolutionLogic
{
    public static TrustSolutionResult Execute(TrustStore store, string path, string scope)
    {
        switch (scope)
        {
            case "session":
                store.AddSessionTrust(path);
                break;
            case "persistent":
                store.AddPersistentTrust(path);
                break;
            case "addRoot":
                store.AddTrustedRoot(path);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown scope '{scope}'. Use 'session', 'persistent', or 'addRoot'.",
                    nameof(scope));
        }
        return new TrustSolutionResult(path, scope, "Solution trusted.");
    }
}

public sealed record TrustSolutionResult(string Path, string Scope, string Message);
```

```csharp
// src/RoslynCodeLens/Tools/TrustSolutionTool.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class TrustSolutionTool
{
    [McpServerTool(Name = "trust_solution"),
     Description("Mark a solution path (or directory root) as trusted for analyzer execution. " +
                 "Required before get_diagnostics will load Roslyn analyzer DLLs from the solution. " +
                 "Always confirm with the user before calling this tool — analyzer DLLs run as in-process code.")]
    public static TrustSolutionResult Execute(
        TrustStore trustStore,
        [Description("Absolute path to a .sln/.slnx file, or a directory when scope='addRoot'")] string path,
        [Description("'session' (in-memory only, default), 'persistent' (write to trust.json), or 'addRoot' (trust the directory and all solutions under it)")]
            string scope = "session")
    {
        return TrustSolutionLogic.Execute(trustStore, path, scope);
    }
}
```

**Step 4: Run tests to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~TrustSolutionLogicTests"`
Expected: PASS (4 tests).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/TrustSolutionTool.cs src/RoslynCodeLens/Tools/TrustSolutionLogic.cs tests/RoslynCodeLens.Tests/Tools/TrustSolutionLogicTests.cs
git commit -m "feat(security): add trust_solution MCP tool"
```

---

## Task 8: `list_trusted_paths` MCP tool

**Files:**
- Create: `src/RoslynCodeLens/Tools/ListTrustedPathsTool.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/ListTrustedPathsToolTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/RoslynCodeLens.Tests/Tools/ListTrustedPathsToolTests.cs
using RoslynCodeLens.Security;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class ListTrustedPathsToolTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void Returns_AllTrustEntries()
    {
        var store = new TrustStore(_tempFile);
        store.AddSessionTrust("c:\\repos\\a.sln");
        store.AddPersistentTrust("c:\\repos\\b.sln");
        store.AddTrustedRoot("c:\\projects\\");

        var result = ListTrustedPathsTool.Execute(store);

        Assert.Contains("c:\\repos\\a.sln", result.SessionSolutions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(result.PersistentSolutions, s => s.Path.Equals("c:\\repos\\b.sln", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("c:\\projects\\", result.TrustedRoots, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("nuget-and-solution-bin", result.AnalyzerPolicy);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ListTrustedPathsToolTests"`
Expected: FAIL.

**Step 3: Write implementation**

```csharp
// src/RoslynCodeLens/Tools/ListTrustedPathsTool.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ListTrustedPathsTool
{
    [McpServerTool(Name = "list_trusted_paths"),
     Description("Return the current trust state: session-scoped paths, persistent paths, trusted roots, and analyzer policy.")]
    public static TrustSnapshot Execute(TrustStore trustStore) => trustStore.GetSnapshot();
}
```

**Step 4: Run test to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ListTrustedPathsToolTests"`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/ListTrustedPathsTool.cs tests/RoslynCodeLens.Tests/Tools/ListTrustedPathsToolTests.cs
git commit -m "feat(security): add list_trusted_paths MCP tool"
```

---

## Task 9: `revoke_trust` MCP tool

**Files:**
- Create: `src/RoslynCodeLens/Tools/RevokeTrustTool.cs`
- Create: `tests/RoslynCodeLens.Tests/Tools/RevokeTrustToolTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/RoslynCodeLens.Tests/Tools/RevokeTrustToolTests.cs
using RoslynCodeLens.Security;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests.Tools;

public class RevokeTrustToolTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"trust-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void RemovesTrust()
    {
        var store = new TrustStore(_tempFile);
        store.AddPersistentTrust("c:\\repos\\foo.sln");
        Assert.True(store.IsTrusted("c:\\repos\\foo.sln"));

        RevokeTrustTool.Execute(store, "c:\\repos\\foo.sln");
        Assert.False(store.IsTrusted("c:\\repos\\foo.sln"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RevokeTrustToolTests"`
Expected: FAIL.

**Step 3: Write implementation**

```csharp
// src/RoslynCodeLens/Tools/RevokeTrustTool.cs
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class RevokeTrustTool
{
    [McpServerTool(Name = "revoke_trust"),
     Description("Remove a previously trusted solution path or trusted root. Removes both session and persistent entries.")]
    public static string Execute(
        TrustStore trustStore,
        [Description("Absolute path of the solution or trusted root to revoke")] string path)
    {
        trustStore.Revoke(path);
        return $"Trust revoked for: {path}";
    }
}
```

**Step 4: Run test to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~RevokeTrustToolTests"`
Expected: PASS.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/RevokeTrustTool.cs tests/RoslynCodeLens.Tests/Tools/RevokeTrustToolTests.cs
git commit -m "feat(security): add revoke_trust MCP tool"
```

---

## Task 10: Wire DI + auto-trust startup solutions in Program.cs

**Files:**
- Modify: `src/RoslynCodeLens/Program.cs`

**Step 1: Modify Program.cs**

```csharp
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoslynCodeLens;
using RoslynCodeLens.Security;

var instance = MSBuildLocator.RegisterDefaults();
var dotnetSdkRoot = instance.MSBuildPath is not null
    ? Path.GetFullPath(Path.Combine(instance.MSBuildPath, "..", "..", ".."))
    : null;

MultiSolutionManager multiManager;

var solutionPaths = args.Length > 0
    ? args.ToList()
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory()) is { } found
        ? [found]
        : [];

if (solutionPaths.Count > 0)
{
    multiManager = await MultiSolutionManager.CreateAsync(solutionPaths).ConfigureAwait(false);
}
else
{
    await Console.Error.WriteLineAsync("[roslyn-codelens] No .sln file found. Tools will return errors.").ConfigureAwait(false);
    multiManager = MultiSolutionManager.CreateEmpty();
}

var trustStore = new TrustStore(TrustStore.DefaultFilePath());
// Auto-trust solutions explicitly handed to us at startup, session-scope only.
// User must call trust_solution(scope="persistent") to keep the trust across server restarts.
foreach (var sln in solutionPaths)
    trustStore.AddSessionTrust(Path.GetFullPath(sln));

var allowlist = new AnalyzerAllowlist(trustStore.AnalyzerPolicy, dotnetSdkRoot);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton(multiManager);
builder.Services.AddSingleton(trustStore);
builder.Services.AddSingleton(allowlist);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
```

**Step 2: Run full test suite + build**

Run: `dotnet build && dotnet test`
Expected: build succeeds, all tests pass.

**Step 3: Smoke-test the server**

Run: `dotnet run --project src/RoslynCodeLens -- "<path-to-this-repo>/RoslynCodeLens.sln"`
Expected: server starts; `[roslyn-codelens] No .sln file found.` is NOT logged.
Kill with Ctrl-C.

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Program.cs
git commit -m "feat(security): register TrustStore+AnalyzerAllowlist; auto-trust startup solutions in session scope"
```

---

## Task 11: SECURITY.md describing the trust model

**Files:**
- Create: `SECURITY.md`

**Step 1: Write the file**

```markdown
# Security Policy

## Reporting a Vulnerability

Use [GitHub Security Advisories](https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/security/advisories/new) — please do not open public issues for security reports.

## Threat Model

This MCP server loads .NET solutions and exposes them to an AI assistant. Two operations execute code from the target solution as a side effect of inspection:

1. **Roslyn analyzers** (DLLs referenced via `<Analyzer Include="..." />` in `.csproj`). These run inside the MCP server process when `get_diagnostics` is called with `includeAnalyzers=true`.
2. **Source generators** (DLLs referenced as analyzers, executed during `Compilation` creation). These run any time a project is compiled — i.e., on every tool invocation that requests a `Compilation`.

Both are arbitrary managed code with the privileges of the user running the MCP server.

## Trust Model

To mitigate untrusted analyzer execution (GHSA-552p-8f74-6x7q):

### Solution trust

- Solutions passed on the CLI at server startup are **session-trusted** automatically (in-memory; lost on restart).
- Other solutions are **untrusted** by default. `get_diagnostics` will refuse `includeAnalyzers=true` for untrusted solutions and instruct the AI to call `trust_solution` after asking the user.
- Trust scopes: `session` (in-memory), `persistent` (saved to `%APPDATA%\roslyn-codelens\trust.json`), `addRoot` (trust a directory prefix).

### Analyzer allowlist

Even for trusted solutions, only analyzer DLLs from known-safe locations are loaded. Default policy `nuget-and-solution-bin` accepts:

- `<userprofile>\.nuget\packages\**` — packages installed by NuGet restore
- `<dotnet-sdk-root>\**` — analyzers shipped with the .NET SDK
- `<solution-dir>\**\bin\**` and `<solution-dir>\**\obj\**` — build output of the solution itself

Stricter alternative: `strict` (NuGet + SDK only). Opt-out: `all` (legacy behavior, equivalent to pre-mitigation).

### Source generators

**Not yet gated.** Source generators are loaded by Roslyn at `Compilation` creation, before any tool runs. See [issue tracker] for follow-up work.

## Known Limitations

- No hash pinning — a poisoned NuGet cache can still inject analyzers.
- Source generator execution is not gated by this trust model (planned).
- Analyzer execution still happens in-process, not sandboxed. Future work may move it to a child process.
```

**Step 2: Commit**

```bash
git add SECURITY.md
git commit -m "docs: add SECURITY.md describing trust model for analyzers"
```

---

## Task 12: README section on the trust model

**Files:**
- Modify: `README.md`

**Step 1: Find current README and inspect structure**

Run: `cat README.md | head -80`

**Step 2: Add a "Security: Trust Model" section near the top**

Insert after the project tagline / installation section:

```markdown
## Security: Trust Model

`get_diagnostics` can load Roslyn analyzers — DLLs that execute in-process. To prevent untrusted analyzers from running automatically, this server uses a VS/Rider-style trust model:

- **Solutions passed on the CLI at startup** are auto-trusted for the current session.
- **Other solutions** must be explicitly trusted via the `trust_solution` MCP tool.
- **Analyzer DLLs** must come from the user's NuGet global packages folder, the dotnet SDK install dir, or the solution's own `bin`/`obj`. Other paths are skipped.

Use the `list_trusted_paths` and `revoke_trust` tools to inspect and manage trust state. Persistent trust is stored at `%APPDATA%\roslyn-codelens\trust.json`.

See [SECURITY.md](SECURITY.md) for the full threat model.
```

**Step 3: Commit**

```bash
git add README.md
git commit -m "docs(readme): document trust model section"
```

---

## Task 13: Skill documentation update

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`

**Step 1: Read current skill content**

Run: `cat plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md | wc -l` then `cat` the file.

**Step 2: Add a section teaching the AI how to handle the trust prompt**

Add near the `get_diagnostics` description:

```markdown
### Trust model for analyzer diagnostics

`get_diagnostics` defaults to `includeAnalyzers=false`. If the user asks for analyzer warnings (StyleCop, Microsoft.CodeAnalysis.Analyzers, etc.):

1. Call `get_diagnostics(includeAnalyzers=true)`.
2. If the server returns an "untrusted solution" error: **ask the user before calling `trust_solution`**. Phrase the question to make it clear that analyzer DLLs run as in-process code, and that they should only trust solutions they wrote or fully vetted.
3. Prefer `scope="session"` for one-off reviews. Only use `scope="persistent"` when the user says they regularly use this solution.
4. Use `list_trusted_paths` to inspect current state when asked.
```

**Step 3: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md
git commit -m "docs(skill): teach AI how to handle the analyzer trust gate"
```

---

## Task 14: End-to-end smoke test against malicious-analyzer fixture

**Files:**
- Create: `tests/RoslynCodeLens.Tests/Fixtures/MaliciousAnalyzerFixture/` (small fixture)
- Create: `tests/RoslynCodeLens.Tests/Security/MaliciousAnalyzerIntegrationTests.cs`

**Step 1: Build a tiny fixture project with an Analyzer reference to a path OUTSIDE `bin/obj`**

Create `tests/RoslynCodeLens.Tests/Fixtures/MaliciousAnalyzerFixture/MaliciousFixture.sln`:
- One project that references an analyzer at `tools/Marker.dll` (don't ship a real DLL — just a non-existent path is fine; we only need to prove the path-classifier rejects it before load).

The test verifies that when this fixture is loaded and analyzers are requested with the default allowlist, the Marker analyzer is never loaded — observable because `AnalyzerRunner.GetAnalyzers` returns empty and no `FileNotFoundException` is thrown (because we filter the reference before `GetAnalyzers()` is called).

**Step 2: Write the test**

```csharp
[Fact]
public async Task DefaultPolicy_SkipsSolutionLocalAnalyzers_NoLoadAttempt()
{
    var fixturePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "MaliciousAnalyzerFixture", "MaliciousFixture.sln"));
    var loaded = await new SolutionLoader().LoadAsync(fixturePath);
    var project = loaded.Solution.Projects.First();
    var compilation = loaded.Compilations[project.Id];

    var allowlist = new AnalyzerAllowlist("nuget-and-solution-bin", dotnetSdkRoot: null);
    var diagnostics = await AnalyzerRunner.RunAnalyzersAsync(project, compilation, allowlist, CancellationToken.None);

    // No load attempted → no exception → empty result.
    Assert.True(diagnostics.IsDefault || diagnostics.IsEmpty);
}
```

**Step 3: Run test**

Run: `dotnet test --filter "FullyQualifiedName~MaliciousAnalyzerIntegrationTests"`
Expected: PASS.

**Step 4: Commit**

```bash
git add tests/RoslynCodeLens.Tests/Fixtures/MaliciousAnalyzerFixture/ tests/RoslynCodeLens.Tests/Security/MaliciousAnalyzerIntegrationTests.cs
git commit -m "test(security): add integration test proving out-of-bounds analyzers are skipped"
```

---

## Task 15: Final verification + advisory remediation note

**Step 1: Full build + test**

Run: `dotnet build && dotnet test`
Expected: 0 errors, 0 warnings, all tests pass.

**Step 2: Smoke test the actual MCP flow**

Start the server with no args in a directory containing a `.sln` and verify:
- Tools list includes `trust_solution`, `list_trusted_paths`, `revoke_trust`, `get_diagnostics`.
- `get_diagnostics(includeAnalyzers=true)` succeeds (auto-trust of cwd solution).
- Pointing the server at an arbitrary solution NOT passed via CLI args returns the untrusted error.

**Step 3: Update the GHSA with remediation status**

After merge, update the advisory comment on GitHub stating the fix is in version X.Y.Z and reference the merged PR.

**Step 4: Final commit if anything outstanding**

```bash
git status   # expect clean
```

---

## Skipped / Out of Scope

- **Hash pinning of analyzer DLLs.** Deferred — not in this plan.
- **Source generator gating.** Same threat primitive; separate change.
- **Sandboxing analyzers in a subprocess.** Architecturally larger; revisit if path-allowlist proves insufficient.
- **MCP elicitation protocol** for an in-protocol trust prompt. Claude Code's permission prompt on the `trust_solution` tool call already provides the human checkpoint; elicitation can be added later if other MCP clients need it.
