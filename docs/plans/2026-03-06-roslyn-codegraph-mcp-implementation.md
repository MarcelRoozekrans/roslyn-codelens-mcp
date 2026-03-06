# Roslyn Code Graph MCP Server — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a Roslyn-based MCP server that loads .NET solutions and exposes 7 semantic query tools over stdio, packaged as a Claude Code plugin.

**Architecture:** A .NET 10 global tool using the `ModelContextProtocol` SDK for stdio transport. On startup it discovers and compiles a `.sln` via `MSBuildWorkspace`, then serves MCP tool requests against the Roslyn semantic model. Distributed as a Claude Code plugin with bootstrap scripts.

**Tech Stack:** .NET 10, Roslyn (`Microsoft.CodeAnalysis.Workspaces.MSBuild`), `ModelContextProtocol` SDK, xUnit for tests.

**Design Doc:** `docs/plans/2026-03-06-roslyn-codegraph-mcp-design.md`

---

## Task 1: Project Scaffolding

**Files:**
- Create: `RoslynCodeGraph.sln`
- Create: `src/RoslynCodeGraph/RoslynCodeGraph.csproj`
- Create: `src/RoslynCodeGraph/Program.cs`
- Create: `tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj`
- Create: `.gitignore`

**Step 1: Create .gitignore**

```bash
cd /c/Projects/Prive/roslyn-codegraph-mcp
cat > .gitignore << 'GITIGNORE'
## .NET
bin/
obj/
*.user
*.suo
*.vs/
.vscode/
*.DotSettings.user

## NuGet
packages/
*.nupkg
project.lock.json

## Build
[Dd]ebug/
[Rr]elease/
TestResults/
GITIGNORE
```

**Step 2: Create solution and projects**

```bash
cd /c/Projects/Prive/roslyn-codegraph-mcp
dotnet new sln -n RoslynCodeGraph
dotnet new console -n RoslynCodeGraph -o src/RoslynCodeGraph
dotnet new xunit -n RoslynCodeGraph.Tests -o tests/RoslynCodeGraph.Tests
dotnet sln add src/RoslynCodeGraph/RoslynCodeGraph.csproj
dotnet sln add tests/RoslynCodeGraph.Tests/RoslynCodeGraph.Tests.csproj
dotnet add tests/RoslynCodeGraph.Tests reference src/RoslynCodeGraph
```

**Step 3: Add NuGet dependencies to main project**

Edit `src/RoslynCodeGraph/RoslynCodeGraph.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>roslyn-codegraph-mcp</ToolCommandName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="0.*" />
    <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="4.*" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.*" />
    <PackageReference Include="Microsoft.Build.Locator" Version="1.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.*" />
  </ItemGroup>
</Project>
```

**Step 4: Write minimal Program.cs (stdio server that starts)**

```csharp
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;

MSBuildLocator.RegisterDefaults();

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

**Step 5: Verify it builds**

```bash
cd /c/Projects/Prive/roslyn-codegraph-mcp
dotnet build
```

Expected: Build succeeds with 0 errors.

**Step 6: Commit**

```bash
git add .gitignore RoslynCodeGraph.sln src/ tests/
git commit -m "chore: scaffold solution with MCP server and test project"
```

---

## Task 2: Shared Models

**Files:**
- Create: `src/RoslynCodeGraph/Models/SymbolLocation.cs`
- Create: `src/RoslynCodeGraph/Models/CallerInfo.cs`
- Create: `src/RoslynCodeGraph/Models/DiRegistration.cs`
- Create: `src/RoslynCodeGraph/Models/TypeHierarchy.cs`
- Create: `src/RoslynCodeGraph/Models/ReflectionUsage.cs`
- Create: `src/RoslynCodeGraph/Models/ProjectDependency.cs`
- Create: `src/RoslynCodeGraph/Models/SymbolContext.cs`

**Step 1: Create all model records**

`src/RoslynCodeGraph/Models/SymbolLocation.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record SymbolLocation(
    string Type,       // "class", "struct", "record"
    string FullName,
    string File,
    int Line,
    string Project);
```

`src/RoslynCodeGraph/Models/CallerInfo.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record CallerInfo(
    string Caller,
    string File,
    int Line,
    string Snippet,
    string Project);
```

`src/RoslynCodeGraph/Models/DiRegistration.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record DiRegistration(
    string Service,
    string Implementation,
    string Lifetime,
    string File,
    int Line);
```

`src/RoslynCodeGraph/Models/TypeHierarchy.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record TypeHierarchy(
    List<SymbolLocation> Bases,
    List<SymbolLocation> Interfaces,
    List<SymbolLocation> Derived);
```

`src/RoslynCodeGraph/Models/ReflectionUsage.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record ReflectionUsage(
    string Kind,     // "dynamic_instantiation", "method_invoke", "assembly_scan", "attribute_discovery"
    string Target,
    string File,
    int Line,
    string Snippet);
```

`src/RoslynCodeGraph/Models/ProjectDependency.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record ProjectDependencyGraph(
    List<ProjectRef> Direct,
    List<ProjectRef> Transitive);

public record ProjectRef(string Name, string Path);
```

`src/RoslynCodeGraph/Models/SymbolContext.cs`:
```csharp
namespace RoslynCodeGraph.Models;

public record SymbolContext(
    string FullName,
    string Namespace,
    string Project,
    string File,
    int Line,
    string? BaseClass,
    List<string> Interfaces,
    List<string> InjectedDependencies,
    List<string> PublicMembers);
```

**Step 2: Verify it builds**

```bash
dotnet build
```

Expected: Build succeeds.

**Step 3: Commit**

```bash
git add src/RoslynCodeGraph/Models/
git commit -m "feat: add shared response model records"
```

---

## Task 3: SolutionLoader

**Files:**
- Create: `src/RoslynCodeGraph/SolutionLoader.cs`
- Create: `tests/RoslynCodeGraph.Tests/SolutionLoaderTests.cs`
- Create: `tests/RoslynCodeGraph.Tests/Fixtures/` (test .sln + .csproj)

**Step 1: Create a minimal test fixture solution**

Create a tiny .NET solution inside the test project for integration tests.

```bash
mkdir -p tests/RoslynCodeGraph.Tests/Fixtures/TestSolution
cd tests/RoslynCodeGraph.Tests/Fixtures/TestSolution
dotnet new sln -n TestSolution
dotnet new classlib -n TestLib -o TestLib
dotnet new classlib -n TestLib2 -o TestLib2
dotnet sln add TestLib/TestLib.csproj
dotnet sln add TestLib2/TestLib2.csproj
dotnet add TestLib2 reference TestLib
```

Then add test source files to the fixture (these provide types/interfaces for all tool tests):

`tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib/IGreeter.cs`:
```csharp
namespace TestLib;

public interface IGreeter
{
    string Greet(string name);
}
```

`tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib/Greeter.cs`:
```csharp
namespace TestLib;

public class Greeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}!";
}
```

`tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib/FancyGreeter.cs`:
```csharp
namespace TestLib;

public class FancyGreeter : Greeter
{
    public override string Greet(string name) => $"Greetings, {name}!";
}
```

`tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib2/GreeterConsumer.cs`:
```csharp
namespace TestLib2;

using TestLib;

public class GreeterConsumer
{
    private readonly IGreeter _greeter;

    public GreeterConsumer(IGreeter greeter)
    {
        _greeter = greeter;
    }

    public string SayHello() => _greeter.Greet("World");
}
```

`tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib2/DiSetup.cs`:
```csharp
namespace TestLib2;

using Microsoft.Extensions.DependencyInjection;
using TestLib;

public static class DiSetup
{
    public static IServiceCollection AddGreeting(this IServiceCollection services)
    {
        services.AddScoped<IGreeter, Greeter>();
        return services;
    }
}
```

Add `Microsoft.Extensions.DependencyInjection.Abstractions` to TestLib2:
```bash
cd tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib2
dotnet add package Microsoft.Extensions.DependencyInjection.Abstractions
```

`tests/RoslynCodeGraph.Tests/Fixtures/TestSolution/TestLib2/ReflectionUser.cs`:
```csharp
namespace TestLib2;

using System;

public class ReflectionUser
{
    public object? CreateDynamic(string typeName)
    {
        var type = Type.GetType(typeName);
        return type != null ? Activator.CreateInstance(type) : null;
    }
}
```

**Step 2: Write the failing test for SolutionLoader**

`tests/RoslynCodeGraph.Tests/SolutionLoaderTests.cs`:
```csharp
using RoslynCodeGraph;

namespace RoslynCodeGraph.Tests;

public class SolutionLoaderTests
{
    [Fact]
    public async Task LoadSolution_ReturnsCompiledSolution()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln");
        fixturePath = Path.GetFullPath(fixturePath);

        var loader = new SolutionLoader();
        var result = await loader.LoadAsync(fixturePath);

        Assert.NotNull(result.Solution);
        Assert.True(result.Compilations.Count >= 2);
    }
}
```

**Step 3: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter SolutionLoaderTests -v n
```

Expected: FAIL — `SolutionLoader` does not exist.

**Step 4: Implement SolutionLoader**

`src/RoslynCodeGraph/SolutionLoader.cs`:
```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynCodeGraph;

public class LoadedSolution
{
    public required Solution Solution { get; init; }
    public required Dictionary<ProjectId, Compilation> Compilations { get; init; }
}

public class SolutionLoader
{
    public async Task<LoadedSolution> LoadAsync(string solutionPath)
    {
        var workspace = MSBuildWorkspace.Create();

        workspace.WorkspaceFailed += (_, e) =>
        {
            Console.Error.WriteLine($"[roslyn-codegraph] Warning: {e.Diagnostic.Message}");
        };

        Console.Error.WriteLine($"[roslyn-codegraph] Loading solution: {Path.GetFileName(solutionPath)}");
        var solution = await workspace.OpenSolutionAsync(solutionPath);

        var compilations = new Dictionary<ProjectId, Compilation>();
        var projects = solution.Projects.ToList();

        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];
            Console.Error.WriteLine(
                $"[roslyn-codegraph] Compiling project {i + 1}/{projects.Count}: {project.Name}");

            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
            {
                compilations[project.Id] = compilation;
            }
        }

        Console.Error.WriteLine(
            $"[roslyn-codegraph] Ready. {compilations.Count} projects compiled.");

        return new LoadedSolution
        {
            Solution = solution,
            Compilations = compilations
        };
    }

    public static string? FindSolutionFile(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir != null)
        {
            var slnFiles = dir.GetFiles("*.sln");
            if (slnFiles.Length > 0)
            {
                return slnFiles
                    .OrderBy(f => f.FullName.Length)
                    .First()
                    .FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
```

**Step 5: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter SolutionLoaderTests -v n
```

Expected: PASS

**Step 6: Commit**

```bash
git add src/RoslynCodeGraph/SolutionLoader.cs tests/
git commit -m "feat: add SolutionLoader with MSBuildWorkspace integration"
```

---

## Task 4: Wire SolutionLoader into DI and Program.cs

**Files:**
- Modify: `src/RoslynCodeGraph/Program.cs`

**Step 1: Update Program.cs to load solution on startup and register it in DI**

```csharp
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using RoslynCodeGraph;

MSBuildLocator.RegisterDefaults();

var solutionPath = args.Length > 0
    ? args[0]
    : SolutionLoader.FindSolutionFile(Directory.GetCurrentDirectory());

if (solutionPath == null)
{
    Console.Error.WriteLine("[roslyn-codegraph] No .sln file found. Tools will return errors.");
}

var loader = new SolutionLoader();
LoadedSolution? loaded = null;

if (solutionPath != null)
{
    loaded = await loader.LoadAsync(solutionPath);
}

var builder = Host.CreateApplicationBuilder(args);

if (loaded != null)
{
    builder.Services.AddSingleton(loaded);
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

**Step 2: Verify it builds**

```bash
dotnet build
```

Expected: Build succeeds.

**Step 3: Commit**

```bash
git add src/RoslynCodeGraph/Program.cs
git commit -m "feat: wire SolutionLoader into DI and startup"
```

---

## Task 5: SymbolFinder Helper

Before building individual tools, create a shared helper that resolves symbol names (simple or fully-qualified) against the loaded compilations.

**Files:**
- Create: `src/RoslynCodeGraph/SymbolResolver.cs`
- Create: `tests/RoslynCodeGraph.Tests/SymbolResolverTests.cs`

**Step 1: Write the failing test**

`tests/RoslynCodeGraph.Tests/SymbolResolverTests.cs`:
```csharp
using RoslynCodeGraph;

namespace RoslynCodeGraph.Tests;

public class SymbolResolverTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindBySimpleName_ReturnsMatches()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindNamedTypes("Greeter");

        Assert.Contains(results, s => s.Name == "Greeter");
    }

    [Fact]
    public void FindByFullName_ReturnsExactMatch()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindNamedTypes("TestLib.Greeter");

        Assert.Single(results);
        Assert.Equal("TestLib.Greeter", results[0].ToDisplayString());
    }

    [Fact]
    public void FindMethods_ReturnsBySymbolName()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindMethods("Greeter.Greet");

        Assert.NotEmpty(results);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter SymbolResolverTests -v n
```

Expected: FAIL — `SymbolResolver` does not exist.

**Step 3: Implement SymbolResolver**

`src/RoslynCodeGraph/SymbolResolver.cs`:
```csharp
using Microsoft.CodeAnalysis;

namespace RoslynCodeGraph;

public class SymbolResolver
{
    private readonly LoadedSolution _loaded;

    public SymbolResolver(LoadedSolution loaded)
    {
        _loaded = loaded;
    }

    public List<INamedTypeSymbol> FindNamedTypes(string symbol)
    {
        var results = new List<INamedTypeSymbol>();
        var hasDot = symbol.Contains('.');

        foreach (var compilation in _loaded.Compilations.Values)
        {
            foreach (var type in GetAllTypes(compilation.GlobalNamespace))
            {
                if (hasDot)
                {
                    if (type.ToDisplayString() == symbol)
                        results.Add(type);
                }
                else
                {
                    if (type.Name == symbol)
                        results.Add(type);
                }
            }
        }

        return results.DistinctBy(t => t.ToDisplayString()).ToList();
    }

    public List<IMethodSymbol> FindMethods(string symbol)
    {
        var results = new List<IMethodSymbol>();
        var parts = symbol.Split('.');
        if (parts.Length < 2) return results;

        var typeName = string.Join('.', parts[..^1]);
        var methodName = parts[^1];

        foreach (var type in FindNamedTypes(typeName))
        {
            results.AddRange(type.GetMembers(methodName).OfType<IMethodSymbol>());
        }

        return results;
    }

    public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in type.GetTypeMembers())
                yield return nested;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            foreach (var type in GetAllTypes(childNs))
                yield return type;
        }
    }

    public Location? GetLocation(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(l => l.IsInSource);
    }

    public (string File, int Line) GetFileAndLine(ISymbol symbol)
    {
        var location = GetLocation(symbol);
        if (location == null) return ("", 0);

        var lineSpan = location.GetLineSpan();
        return (lineSpan.Path, lineSpan.StartLinePosition.Line + 1);
    }

    public string GetProjectName(ISymbol symbol)
    {
        var location = GetLocation(symbol);
        if (location?.SourceTree == null) return "";

        var filePath = location.SourceTree.FilePath;
        foreach (var project in _loaded.Solution.Projects)
        {
            if (project.Documents.Any(d => d.FilePath == filePath))
                return project.Name;
        }
        return "";
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter SymbolResolverTests -v n
```

Expected: PASS

**Step 5: Register SymbolResolver in DI (update Program.cs)**

Add after `builder.Services.AddSingleton(loaded);`:
```csharp
builder.Services.AddSingleton<SymbolResolver>();
```

**Step 6: Commit**

```bash
git add src/RoslynCodeGraph/SymbolResolver.cs tests/RoslynCodeGraph.Tests/SymbolResolverTests.cs src/RoslynCodeGraph/Program.cs
git commit -m "feat: add SymbolResolver for type and method lookup"
```

---

## Task 6: find_implementations Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/FindImplementationsTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/FindImplementationsToolTests.cs`

**Step 1: Write the failing test**

`tests/RoslynCodeGraph.Tests/Tools/FindImplementationsToolTests.cs`:
```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindImplementationsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindImplementations_ForInterface_ReturnsImplementors()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, "IGreeter");

        Assert.Contains(results, r => r.FullName.Contains("Greeter"));
        Assert.True(results.Count >= 1);
    }

    [Fact]
    public void FindImplementations_ForBaseClass_ReturnsDerived()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, "Greeter");

        Assert.Contains(results, r => r.FullName.Contains("FancyGreeter"));
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter FindImplementationsToolTests -v n
```

Expected: FAIL

**Step 3: Implement the tool**

`src/RoslynCodeGraph/Tools/FindImplementationsTool.cs`:
```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindImplementationsLogic
{
    public static List<SymbolLocation> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var types = resolver.FindNamedTypes(symbol);
        var results = new List<SymbolLocation>();

        foreach (var type in types)
        {
            foreach (var candidateType in loaded.Compilations.Values
                .SelectMany(c => SymbolResolver.GetAllTypes(c.GlobalNamespace)))
            {
                bool isMatch = false;

                if (type.TypeKind == TypeKind.Interface)
                {
                    isMatch = candidateType.AllInterfaces.Any(i =>
                        SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, type.OriginalDefinition));
                }
                else
                {
                    var baseType = candidateType.BaseType;
                    while (baseType != null)
                    {
                        if (SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, type.OriginalDefinition))
                        {
                            isMatch = true;
                            break;
                        }
                        baseType = baseType.BaseType;
                    }
                }

                if (isMatch)
                {
                    var (file, line) = resolver.GetFileAndLine(candidateType);
                    results.Add(new SymbolLocation(
                        candidateType.TypeKind.ToString().ToLowerInvariant(),
                        candidateType.ToDisplayString(),
                        file,
                        line,
                        resolver.GetProjectName(candidateType)));
                }
            }
        }

        return results.DistinctBy(r => r.FullName).ToList();
    }
}

[McpServerToolType]
public static class FindImplementationsTool
{
    [McpServerTool("find_implementations"),
     Description("Find all classes/structs implementing an interface or extending a class")]
    public static List<SymbolLocation> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        return FindImplementationsLogic.Execute(loaded, resolver, symbol);
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter FindImplementationsToolTests -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/FindImplementationsTool.cs tests/RoslynCodeGraph.Tests/Tools/
git commit -m "feat: add find_implementations MCP tool"
```

---

## Task 7: find_callers Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/FindCallersTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/FindCallersToolTests.cs`

**Step 1: Write the failing test**

`tests/RoslynCodeGraph.Tests/Tools/FindCallersToolTests.cs`:
```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindCallersToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindCallers_ForMethod_ReturnsCallSites()
    {
        var results = FindCallersLogic.Execute(_loaded, _resolver, "IGreeter.Greet");

        Assert.Contains(results, r => r.Caller.Contains("GreeterConsumer"));
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter FindCallersToolTests -v n
```

Expected: FAIL

**Step 3: Implement the tool**

`src/RoslynCodeGraph/Tools/FindCallersTool.cs`:
```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindCallersLogic
{
    public static List<CallerInfo> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var targetMethods = resolver.FindMethods(symbol);
        if (targetMethods.Count == 0) return [];

        var results = new List<CallerInfo>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var project = loaded.Solution.GetProject(projectId);
            if (project == null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    var calledSymbol = symbolInfo.Symbol as IMethodSymbol;
                    if (calledSymbol == null) continue;

                    var isMatch = targetMethods.Any(target =>
                        SymbolEqualityComparer.Default.Equals(calledSymbol.OriginalDefinition, target.OriginalDefinition) ||
                        calledSymbol.OriginalDefinition.ContainingType.AllInterfaces
                            .SelectMany(i => i.GetMembers(target.Name).OfType<IMethodSymbol>())
                            .Any(im => SymbolEqualityComparer.Default.Equals(
                                calledSymbol.OriginalDefinition,
                                calledSymbol.OriginalDefinition.ContainingType.FindImplementationForInterfaceMember(im))));

                    if (!isMatch) continue;

                    var containingMethod = invocation.Ancestors()
                        .OfType<MethodDeclarationSyntax>().FirstOrDefault();
                    var callerName = containingMethod != null
                        ? $"{semanticModel.GetDeclaredSymbol(containingMethod.Parent as TypeDeclarationSyntax ?? containingMethod.Parent as TypeDeclarationSyntax!)?.Name ?? "?"}.{containingMethod.Identifier.Text}"
                        : "?";

                    var lineSpan = tree.GetLineSpan(invocation.Span);
                    var snippet = invocation.ToString();
                    if (snippet.Length > 120) snippet = snippet[..120] + "...";

                    results.Add(new CallerInfo(
                        callerName,
                        tree.FilePath,
                        lineSpan.StartLinePosition.Line + 1,
                        snippet,
                        project.Name));
                }
            }
        }

        return results;
    }
}

[McpServerToolType]
public static class FindCallersTool
{
    [McpServerTool("find_callers"),
     Description("Find every call site for a method, property, or constructor")]
    public static List<CallerInfo> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Method name as Type.Method (simple or fully qualified)")] string symbol)
    {
        return FindCallersLogic.Execute(loaded, resolver, symbol);
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter FindCallersToolTests -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/FindCallersTool.cs tests/RoslynCodeGraph.Tests/Tools/FindCallersToolTests.cs
git commit -m "feat: add find_callers MCP tool"
```

---

## Task 8: get_type_hierarchy Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/GetTypeHierarchyTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/GetTypeHierarchyToolTests.cs`

**Step 1: Write the failing test**

`tests/RoslynCodeGraph.Tests/Tools/GetTypeHierarchyToolTests.cs`:
```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetTypeHierarchyToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void GetHierarchy_ForGreeter_ShowsBaseAndDerived()
    {
        var result = GetTypeHierarchyLogic.Execute(_loaded, _resolver, "Greeter");

        Assert.NotNull(result);
        Assert.Contains(result.Interfaces, i => i.FullName.Contains("IGreeter"));
        Assert.Contains(result.Derived, d => d.FullName.Contains("FancyGreeter"));
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter GetTypeHierarchyToolTests -v n
```

Expected: FAIL

**Step 3: Implement the tool**

`src/RoslynCodeGraph/Tools/GetTypeHierarchyTool.cs`:
```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetTypeHierarchyLogic
{
    public static TypeHierarchy? Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var types = resolver.FindNamedTypes(symbol);
        if (types.Count == 0) return null;

        var type = types[0];
        var bases = new List<SymbolLocation>();
        var interfaces = new List<SymbolLocation>();
        var derived = new List<SymbolLocation>();

        // Walk base classes
        var baseType = type.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            var (file, line) = resolver.GetFileAndLine(baseType);
            bases.Add(new SymbolLocation(
                baseType.TypeKind.ToString().ToLowerInvariant(),
                baseType.ToDisplayString(),
                file, line,
                resolver.GetProjectName(baseType)));
            baseType = baseType.BaseType;
        }

        // Interfaces
        foreach (var iface in type.AllInterfaces)
        {
            var (file, line) = resolver.GetFileAndLine(iface);
            interfaces.Add(new SymbolLocation(
                "interface",
                iface.ToDisplayString(),
                file, line,
                resolver.GetProjectName(iface)));
        }

        // Derived types
        foreach (var candidate in loaded.Compilations.Values
            .SelectMany(c => SymbolResolver.GetAllTypes(c.GlobalNamespace)))
        {
            var bt = candidate.BaseType;
            while (bt != null)
            {
                if (SymbolEqualityComparer.Default.Equals(bt.OriginalDefinition, type.OriginalDefinition))
                {
                    var (file, line) = resolver.GetFileAndLine(candidate);
                    derived.Add(new SymbolLocation(
                        candidate.TypeKind.ToString().ToLowerInvariant(),
                        candidate.ToDisplayString(),
                        file, line,
                        resolver.GetProjectName(candidate)));
                    break;
                }
                bt = bt.BaseType;
            }
        }

        return new TypeHierarchy(bases, interfaces, derived.DistinctBy(d => d.FullName).ToList());
    }
}

[McpServerToolType]
public static class GetTypeHierarchyTool
{
    [McpServerTool("get_type_hierarchy"),
     Description("Walk up (base classes, interfaces) and down (derived types) from a type")]
    public static TypeHierarchy? Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        return GetTypeHierarchyLogic.Execute(loaded, resolver, symbol);
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter GetTypeHierarchyToolTests -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/GetTypeHierarchyTool.cs tests/RoslynCodeGraph.Tests/Tools/GetTypeHierarchyToolTests.cs
git commit -m "feat: add get_type_hierarchy MCP tool"
```

---

## Task 9: get_di_registrations Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/GetDiRegistrationsTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/GetDiRegistrationsToolTests.cs`

**Step 1: Write the failing test**

`tests/RoslynCodeGraph.Tests/Tools/GetDiRegistrationsToolTests.cs`:
```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetDiRegistrationsToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindDiRegistrations_ForIGreeter_ReturnsRegistration()
    {
        var results = GetDiRegistrationsLogic.Execute(_loaded, _resolver, "IGreeter");

        Assert.Single(results);
        Assert.Equal("Scoped", results[0].Lifetime);
        Assert.Contains("Greeter", results[0].Implementation);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter GetDiRegistrationsToolTests -v n
```

Expected: FAIL

**Step 3: Implement the tool**

`src/RoslynCodeGraph/Tools/GetDiRegistrationsTool.cs`:
```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetDiRegistrationsLogic
{
    private static readonly HashSet<string> DiMethodNames = new()
    {
        "AddTransient", "AddScoped", "AddSingleton",
        "TryAddTransient", "TryAddScoped", "TryAddSingleton",
        "AddKeyedTransient", "AddKeyedScoped", "AddKeyedSingleton"
    };

    public static List<DiRegistration> Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var results = new List<DiRegistration>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    var methodName = GetMethodName(invocation);
                    if (methodName == null || !DiMethodNames.Contains(methodName)) continue;

                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol method) continue;

                    string? serviceName = null;
                    string? implName = null;

                    // Check generic type arguments: AddScoped<IService, Impl>()
                    if (method.TypeArguments.Length == 2)
                    {
                        serviceName = method.TypeArguments[0].Name;
                        implName = method.TypeArguments[1].Name;
                    }
                    else if (method.TypeArguments.Length == 1)
                    {
                        serviceName = method.TypeArguments[0].Name;
                        implName = method.TypeArguments[0].Name;
                    }

                    if (serviceName == null) continue;

                    // Filter to requested symbol
                    if (!string.IsNullOrEmpty(symbol) &&
                        !serviceName.Contains(symbol.Split('.').Last()) &&
                        !implName?.Contains(symbol.Split('.').Last()) == true)
                        continue;

                    var lifetime = methodName.Replace("TryAdd", "").Replace("Add", "").Replace("Keyed", "");
                    var lineSpan = tree.GetLineSpan(invocation.Span);

                    results.Add(new DiRegistration(
                        serviceName,
                        implName ?? serviceName,
                        lifetime,
                        tree.FilePath,
                        lineSpan.StartLinePosition.Line + 1));
                }
            }
        }

        return results;
    }

    private static string? GetMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => null
        };
    }
}

[McpServerToolType]
public static class GetDiRegistrationsTool
{
    [McpServerTool("get_di_registrations"),
     Description("Scan IServiceCollection extension methods for DI registrations of a type")]
    public static List<DiRegistration> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Type name to search for (simple or fully qualified)")] string symbol)
    {
        return GetDiRegistrationsLogic.Execute(loaded, resolver, symbol);
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter GetDiRegistrationsToolTests -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/GetDiRegistrationsTool.cs tests/RoslynCodeGraph.Tests/Tools/GetDiRegistrationsToolTests.cs
git commit -m "feat: add get_di_registrations MCP tool"
```

---

## Task 10: get_project_dependencies Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/GetProjectDependenciesTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/GetProjectDependenciesToolTests.cs`

**Step 1: Write the failing test**

`tests/RoslynCodeGraph.Tests/Tools/GetProjectDependenciesToolTests.cs`:
```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetProjectDependenciesToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void GetDependencies_ForTestLib2_ReturnsTestLib()
    {
        var result = GetProjectDependenciesLogic.Execute(_loaded, "TestLib2");

        Assert.NotNull(result);
        Assert.Contains(result.Direct, d => d.Name == "TestLib");
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter GetProjectDependenciesToolTests -v n
```

Expected: FAIL

**Step 3: Implement the tool**

`src/RoslynCodeGraph/Tools/GetProjectDependenciesTool.cs`:
```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetProjectDependenciesLogic
{
    public static ProjectDependencyGraph? Execute(LoadedSolution loaded, string projectName)
    {
        var project = loaded.Solution.Projects
            .FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)
                || (p.FilePath ?? "").Contains(projectName, StringComparison.OrdinalIgnoreCase));

        if (project == null) return null;

        var direct = new List<ProjectRef>();
        var transitive = new HashSet<string>();

        foreach (var refId in project.ProjectReferences.Select(r => r.ProjectId))
        {
            var refProject = loaded.Solution.GetProject(refId);
            if (refProject == null) continue;
            direct.Add(new ProjectRef(refProject.Name, refProject.FilePath ?? ""));
            CollectTransitive(loaded.Solution, refProject, transitive);
        }

        var transitiveList = transitive
            .Except(direct.Select(d => d.Name))
            .Select(name =>
            {
                var p = loaded.Solution.Projects.FirstOrDefault(pr => pr.Name == name);
                return new ProjectRef(name, p?.FilePath ?? "");
            })
            .ToList();

        return new ProjectDependencyGraph(direct, transitiveList);
    }

    private static void CollectTransitive(Solution solution, Project project, HashSet<string> visited)
    {
        foreach (var refId in project.ProjectReferences.Select(r => r.ProjectId))
        {
            var refProject = solution.GetProject(refId);
            if (refProject == null || !visited.Add(refProject.Name)) continue;
            CollectTransitive(solution, refProject, visited);
        }
    }
}

[McpServerToolType]
public static class GetProjectDependenciesTool
{
    [McpServerTool("get_project_dependencies"),
     Description("Return the project reference graph (direct and transitive dependencies)")]
    public static ProjectDependencyGraph? Execute(
        LoadedSolution loaded,
        [Description("Project name or .csproj filename")] string project)
    {
        return GetProjectDependenciesLogic.Execute(loaded, project);
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter GetProjectDependenciesToolTests -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/GetProjectDependenciesTool.cs tests/RoslynCodeGraph.Tests/Tools/GetProjectDependenciesToolTests.cs
git commit -m "feat: add get_project_dependencies MCP tool"
```

---

## Task 11: get_symbol_context Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/GetSymbolContextTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/GetSymbolContextToolTests.cs`

**Step 1: Write the failing test**

`tests/RoslynCodeGraph.Tests/Tools/GetSymbolContextToolTests.cs`:
```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class GetSymbolContextToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void GetContext_ForGreeterConsumer_ShowsInjectedDeps()
    {
        var result = GetSymbolContextLogic.Execute(_loaded, _resolver, "GreeterConsumer");

        Assert.NotNull(result);
        Assert.Equal("TestLib2", result.Namespace);
        Assert.Contains(result.InjectedDependencies, d => d.Contains("IGreeter"));
        Assert.Contains(result.PublicMembers, m => m.Contains("SayHello"));
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter GetSymbolContextToolTests -v n
```

Expected: FAIL

**Step 3: Implement the tool**

`src/RoslynCodeGraph/Tools/GetSymbolContextTool.cs`:
```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class GetSymbolContextLogic
{
    public static SymbolContext? Execute(LoadedSolution loaded, SymbolResolver resolver, string symbol)
    {
        var types = resolver.FindNamedTypes(symbol);
        if (types.Count == 0) return null;

        var type = types[0];
        var (file, line) = resolver.GetFileAndLine(type);

        var baseClass = type.BaseType is { SpecialType: not SpecialType.System_Object }
            ? type.BaseType.ToDisplayString()
            : null;

        var interfaces = type.AllInterfaces
            .Select(i => i.ToDisplayString())
            .ToList();

        // Find constructor-injected dependencies
        var injected = type.Constructors
            .Where(c => !c.IsImplicitlyDeclared)
            .SelectMany(c => c.Parameters)
            .Select(p => $"{p.Type.ToDisplayString()} {p.Name}")
            .ToList();

        var publicMembers = type.GetMembers()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public
                && !m.IsImplicitlyDeclared
                && m is not IMethodSymbol { MethodKind: MethodKind.Constructor })
            .Select(m => m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
            .ToList();

        return new SymbolContext(
            type.ToDisplayString(),
            type.ContainingNamespace.ToDisplayString(),
            resolver.GetProjectName(type),
            file,
            line,
            baseClass,
            interfaces,
            injected,
            publicMembers);
    }
}

[McpServerToolType]
public static class GetSymbolContextTool
{
    [McpServerTool("get_symbol_context"),
     Description("One-shot context dump for a type: namespace, base class, interfaces, injected dependencies, public members")]
    public static SymbolContext? Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Type name (simple or fully qualified)")] string symbol)
    {
        return GetSymbolContextLogic.Execute(loaded, resolver, symbol);
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter GetSymbolContextToolTests -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/GetSymbolContextTool.cs tests/RoslynCodeGraph.Tests/Tools/GetSymbolContextToolTests.cs
git commit -m "feat: add get_symbol_context MCP tool"
```

---

## Task 12: find_reflection_usage Tool

**Files:**
- Create: `src/RoslynCodeGraph/Tools/FindReflectionUsageTool.cs`
- Create: `tests/RoslynCodeGraph.Tests/Tools/FindReflectionUsageToolTests.cs`

**Step 1: Write the failing test**

`tests/RoslynCodeGraph.Tests/Tools/FindReflectionUsageToolTests.cs`:
```csharp
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Tests.Tools;

public class FindReflectionUsageToolTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.sln"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindReflection_DetectsActivatorCreateInstance()
    {
        var results = FindReflectionUsageLogic.Execute(_loaded, _resolver, null);

        Assert.Contains(results, r => r.Kind == "dynamic_instantiation"
            && r.Snippet.Contains("Activator.CreateInstance"));
    }

    [Fact]
    public void FindReflection_DetectsTypeGetType()
    {
        var results = FindReflectionUsageLogic.Execute(_loaded, _resolver, null);

        Assert.Contains(results, r => r.Snippet.Contains("Type.GetType"));
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter FindReflectionUsageToolTests -v n
```

Expected: FAIL

**Step 3: Implement the tool**

`src/RoslynCodeGraph/Tools/FindReflectionUsageTool.cs`:
```csharp
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using RoslynCodeGraph.Models;

namespace RoslynCodeGraph.Tools;

public static class FindReflectionUsageLogic
{
    private static readonly Dictionary<string, string> ReflectionMethods = new()
    {
        { "GetType", "dynamic_instantiation" },
        { "CreateInstance", "dynamic_instantiation" },
        { "Invoke", "method_invoke" },
        { "GetMethod", "method_invoke" },
        { "GetTypes", "assembly_scan" },
        { "GetExportedTypes", "assembly_scan" },
        { "GetCustomAttributes", "attribute_discovery" },
        { "GetCustomAttribute", "attribute_discovery" },
    };

    public static List<ReflectionUsage> Execute(LoadedSolution loaded, SymbolResolver resolver, string? symbol)
    {
        var results = new List<ReflectionUsage>();

        foreach (var (projectId, compilation) in loaded.Compilations)
        {
            var project = loaded.Solution.GetProject(projectId);

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
                    if (memberAccess == null) continue;

                    var methodName = memberAccess.Name.Identifier.Text;
                    if (!ReflectionMethods.TryGetValue(methodName, out var kind)) continue;

                    // Verify it's actually a reflection API call
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    var calledMethod = symbolInfo.Symbol as IMethodSymbol;
                    if (calledMethod == null) continue;

                    var containingType = calledMethod.ContainingType?.ToDisplayString() ?? "";
                    if (!IsReflectionType(containingType)) continue;

                    var snippet = invocation.ToString();
                    if (snippet.Length > 120) snippet = snippet[..120] + "...";

                    // Try to determine target type
                    var target = ExtractTarget(invocation, semanticModel) ?? "";

                    // Filter by symbol if specified
                    if (!string.IsNullOrEmpty(symbol) && !target.Contains(symbol, StringComparison.OrdinalIgnoreCase)
                        && !snippet.Contains(symbol, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var lineSpan = tree.GetLineSpan(invocation.Span);
                    results.Add(new ReflectionUsage(
                        kind,
                        target,
                        tree.FilePath,
                        lineSpan.StartLinePosition.Line + 1,
                        snippet));
                }
            }
        }

        return results;
    }

    private static bool IsReflectionType(string typeName) =>
        typeName is "System.Type" or "System.Activator" or "System.Reflection.MethodInfo"
            or "System.Reflection.Assembly" or "System.Reflection.MemberInfo"
            || typeName.StartsWith("System.Reflection.");

    private static string? ExtractTarget(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0) return null;

        var firstArg = args[0].Expression;
        if (firstArg is LiteralExpressionSyntax literal)
            return literal.Token.ValueText;

        var constant = model.GetConstantValue(firstArg);
        return constant.HasValue ? constant.Value?.ToString() : null;
    }
}

[McpServerToolType]
public static class FindReflectionUsageTool
{
    [McpServerTool("find_reflection_usage"),
     Description("Detect dynamic/reflection-based usage like Type.GetType, Activator.CreateInstance, MethodInfo.Invoke, assembly scanning, attribute discovery")]
    public static List<ReflectionUsage> Execute(
        LoadedSolution loaded,
        SymbolResolver resolver,
        [Description("Optional type name to filter results (omit to scan entire solution)")] string? symbol = null)
    {
        return FindReflectionUsageLogic.Execute(loaded, resolver, symbol);
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test tests/RoslynCodeGraph.Tests --filter FindReflectionUsageToolTests -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/RoslynCodeGraph/Tools/FindReflectionUsageTool.cs tests/RoslynCodeGraph.Tests/Tools/FindReflectionUsageToolTests.cs
git commit -m "feat: add find_reflection_usage MCP tool"
```

---

## Task 13: Full Test Suite Green Check

**Step 1: Run all tests**

```bash
cd /c/Projects/Prive/roslyn-codegraph-mcp
dotnet test --verbosity normal
```

Expected: All tests pass.

**Step 2: Run the server manually to verify it starts**

```bash
cd tests/RoslynCodeGraph.Tests/Fixtures/TestSolution
dotnet run --project ../../../../src/RoslynCodeGraph/RoslynCodeGraph.csproj
```

Expected: stderr shows solution loading progress, server waits for stdin.

**Step 3: Commit (if any fixes were needed)**

```bash
git add -A
git commit -m "fix: ensure full test suite passes"
```

---

## Task 14: Plugin Manifests and Bootstrap Scripts

**Files:**
- Create: `.claude-plugin/marketplace.json`
- Create: `plugins/roslyn-codegraph/.claude-plugin/plugin.json`
- Create: `plugins/roslyn-codegraph/bootstrap.sh`
- Create: `plugins/roslyn-codegraph/bootstrap.ps1`

**Step 1: Create marketplace manifest**

`.claude-plugin/marketplace.json`:
```json
{
  "name": "roslyn-codegraph-mcp",
  "description": "Roslyn-based .NET code graph intelligence for Claude Code",
  "version": "1.0.0",
  "owner": {
    "name": "Marcel Roozekrans"
  },
  "plugins": [
    {
      "name": "roslyn-codegraph",
      "description": "Roslyn-based code graph intelligence for .NET codebases. Provides semantic understanding of type hierarchies, call sites, DI registrations, and reflection usage.",
      "version": "1.0.0",
      "author": {
        "name": "Marcel Roozekrans"
      },
      "source": "./plugins/roslyn-codegraph",
      "category": "code-intelligence"
    }
  ]
}
```

**Step 2: Create plugin manifest**

`plugins/roslyn-codegraph/.claude-plugin/plugin.json`:
```json
{
  "name": "roslyn-codegraph",
  "description": "Roslyn-based code graph intelligence for .NET codebases.",
  "author": {
    "name": "Marcel Roozekrans"
  },
  "mcp_servers": {
    "roslyn-codegraph": {
      "command": "bootstrap",
      "args": [],
      "transport": "stdio"
    }
  }
}
```

**Step 3: Create bootstrap.sh**

`plugins/roslyn-codegraph/bootstrap.sh`:
```bash
#!/usr/bin/env bash
set -euo pipefail

TOOL_NAME="roslyn-codegraph-mcp"

if ! command -v "$TOOL_NAME" &>/dev/null; then
    echo "[roslyn-codegraph] Installing $TOOL_NAME dotnet global tool..." >&2
    dotnet tool install -g "$TOOL_NAME" >&2
fi

exec "$TOOL_NAME" "$@"
```

**Step 4: Create bootstrap.ps1**

`plugins/roslyn-codegraph/bootstrap.ps1`:
```powershell
$ErrorActionPreference = "Stop"
$ToolName = "roslyn-codegraph-mcp"

$installed = dotnet tool list -g | Select-String $ToolName
if (-not $installed) {
    Write-Host "[roslyn-codegraph] Installing $ToolName dotnet global tool..." -ForegroundColor Yellow
    dotnet tool install -g $ToolName
}

& $ToolName @args
```

**Step 5: Make bootstrap.sh executable**

```bash
chmod +x plugins/roslyn-codegraph/bootstrap.sh
```

**Step 6: Commit**

```bash
git add .claude-plugin/ plugins/
git commit -m "feat: add plugin manifests and bootstrap scripts"
```

---

## Task 15: Skill File

**Files:**
- Create: `plugins/roslyn-codegraph/skills/roslyn-codegraph/SKILL.md`

**Step 1: Create the skill**

`plugins/roslyn-codegraph/skills/roslyn-codegraph/SKILL.md`:
```markdown
---
name: roslyn-codegraph
description: Enhances brainstorming and refactor-analysis with Roslyn-powered semantic code intelligence for .NET codebases. Activates automatically when roslyn-codegraph MCP tools are available.
---

# Roslyn Code Graph Intelligence

## Detection

Check if `find_implementations` is available as an MCP tool. If not, this skill is inert — do nothing.

## During Brainstorming

When brainstorming about a .NET codebase:

1. **At start** — Call `get_project_dependencies` on the main project to understand solution architecture. Call `get_symbol_context` on any types mentioned in the request.

2. **During clarifying questions** — Use `find_implementations`, `get_type_hierarchy`, and `find_callers` to ground questions in actual architecture rather than assumptions.

3. **When proposing approaches** — Call `get_di_registrations` for current DI wiring, `find_reflection_usage` for hidden coupling, `get_type_hierarchy` for extension points.

4. **During design presentation** — Reference concrete types, interfaces, and call sites. Example: "These 3 classes implement IUserService: UserService, CachedUserService, AdminUserService."

## During Refactor Analysis

When analyzing refactors in a .NET codebase:

- **Direct Dependency Mapping:** Use `find_callers` + `find_implementations` instead of Grep for accurate dependency tracking.
- **Transitive Closure:** Use `get_type_hierarchy` + `get_project_dependencies` for semantic traversal instead of text search.
- **Risk Identification:** Use `find_reflection_usage` to detect dynamic/hidden coupling that text search misses.

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
```

**Step 2: Commit**

```bash
git add plugins/roslyn-codegraph/skills/
git commit -m "feat: add roslyn-codegraph skill for brainstorming and refactor enhancement"
```

---

## Task 16: README

**Files:**
- Create: `README.md`

**Step 1: Write README**

```markdown
# Roslyn Code Graph MCP Server

A Roslyn-based MCP server that provides semantic code intelligence for .NET codebases. Designed for use with Claude Code to understand type hierarchies, call sites, DI registrations, and reflection usage.

## Features

- **find_implementations** — Find all classes/structs implementing an interface or extending a class
- **find_callers** — Find every call site for a method, property, or constructor
- **get_type_hierarchy** — Walk base classes, interfaces, and derived types
- **get_di_registrations** — Scan for DI service registrations
- **get_project_dependencies** — Get the project reference graph
- **get_symbol_context** — One-shot context dump for any type
- **find_reflection_usage** — Detect dynamic/reflection-based usage

## Installation

### As a Claude Code Plugin

```bash
claude install gh:MarcelRoozekrans/roslyn-codegraph-mcp
```

### As a .NET Global Tool

```bash
dotnet tool install -g roslyn-codegraph-mcp
```

### Manual MCP Configuration

Add to your Claude Code MCP settings:

```json
{
  "mcpServers": {
    "roslyn-codegraph": {
      "command": "roslyn-codegraph-mcp",
      "args": [],
      "transport": "stdio"
    }
  }
}
```

## Usage

The server automatically discovers `.sln` files by walking up from the current directory. You can also pass a solution path directly:

```bash
roslyn-codegraph-mcp /path/to/MySolution.sln
```

## Requirements

- .NET 10 SDK
- A .NET solution with compilable projects

## Development

```bash
dotnet build
dotnet test
```

## License

MIT
```

**Step 2: Create LICENSE**

```bash
# Use standard MIT license text
```

**Step 3: Commit**

```bash
git add README.md LICENSE
git commit -m "docs: add README and LICENSE"
```

---

## Summary

| Task | Component | Tests |
|------|-----------|-------|
| 1 | Project scaffolding | Build check |
| 2 | Shared models | Build check |
| 3 | SolutionLoader | SolutionLoaderTests |
| 4 | Program.cs DI wiring | Build check |
| 5 | SymbolResolver | SymbolResolverTests |
| 6 | find_implementations | FindImplementationsToolTests |
| 7 | find_callers | FindCallersToolTests |
| 8 | get_type_hierarchy | GetTypeHierarchyToolTests |
| 9 | get_di_registrations | GetDiRegistrationsToolTests |
| 10 | get_project_dependencies | GetProjectDependenciesToolTests |
| 11 | get_symbol_context | GetSymbolContextToolTests |
| 12 | find_reflection_usage | FindReflectionUsageToolTests |
| 13 | Full test suite green | All tests |
| 14 | Plugin manifests + bootstrap | Manual verification |
| 15 | Skill file | Manual verification |
| 16 | README + LICENSE | — |
