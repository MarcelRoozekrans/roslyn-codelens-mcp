# resolve_stack_trace Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** A `resolve_stack_trace` MCP tool that maps a pasted .NET stack trace to file/line/symbol against the loaded solution, undoing compiler name mangling (async/iterator state machines, lambdas, local functions, generics).

**Architecture:** Three pure layers + a thin tool wrapper, following the repo's Tool/Logic split. `StackTraceParser` (text → parsed lines) and `StackFrameDemangler` (mangled type/method → logical target) are pure static classes with no Roslyn dependency — fully unit-testable. `ResolveStackTraceLogic` resolves demangled targets through the existing `SymbolResolver` (exact + arity-stripped indexes) and `MetadataSymbolResolver`, and returns the standard list envelope. Design doc: `docs/plans/2026-07-19-resolve-stack-trace-design.md` (read it first — it fixes the frame model, demangling table, and resolution order).

**Tech Stack:** .NET / C#, existing Roslyn workspaces infra (no new dependencies), xUnit; resolution tests use `tests/RoslynCodeLens.Tests/Fixtures/RenameTestWorkspace.cs` (AdhocWorkspace, isolated from the shared fixture).

**Working directory:** the `.worktrees/resolve-stack-trace` worktree, branch `feature/resolve-stack-trace`. All paths relative to that root; run all `dotnet`/`git` commands there.

**Codebase conventions:**
- Errors: `throw new McpToolException(ToolErrorCode.X, message, detailsObject)`.
- List tools return `ToolListResult.Create(items, limit, summary)` (`src/RoslynCodeLens/Models/ToolListResult.cs`); copy the summary pattern from `FindReferencesTool.BuildSummary` (an anonymous object).
- Tools: `[McpServerToolType]` static class + `[McpServerTool(Name = "...")]`; get context via `manager.EnsureLoaded(); var context = manager.GetAnalysisContext();` (`context.Loaded`, `context.Resolver`, `context.Metadata` — verify the third member's exact name in `SolutionAnalysisContext.cs` before use).
- String kinds (like `SymbolReference`'s `kind`), not enums, for JSON friendliness.
- Commits: hooks must pass (never `--no-verify`); end every message with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

---

### Task 1: Models

**Files:**
- Create: `src/RoslynCodeLens/Models/StackFrameInfo.cs`

**Step 1: Write the record**

```csharp
namespace RoslynCodeLens.Models;

/// <summary>
/// One resolved element of a pasted stack trace, in original trace order.
/// Kind: exception | method | asyncMethod | iterator | lambda | localFunction | constructor | unknown.
/// Origin: source | metadata | unresolved.
/// </summary>
public record StackFrameInfo(
    int Index,
    string Raw,
    string Kind,
    string Symbol,
    string? EnclosingMethod,
    string? File,
    int? Line,
    string Origin,
    string? Project);
```

**Step 2: Build + commit**

Run: `dotnet build src/RoslynCodeLens` → 0 errors.

```bash
git add src/RoslynCodeLens/Models/StackFrameInfo.cs
git commit -m "feat: StackFrameInfo model for resolve_stack_trace"
```

---

### Task 2: StackTraceParser (TDD)

Pure text → structured lines. No demangling here, no Roslyn.

**Files:**
- Create: `tests/RoslynCodeLens.Tests/StackTraceParserTests.cs`
- Create: `src/RoslynCodeLens/StackTrace/StackTraceParser.cs`

**Step 1: Write the failing tests**

```csharp
using RoslynCodeLens.StackTrace;

namespace RoslynCodeLens.Tests;

public class StackTraceParserTests
{
    [Fact]
    public void RuntimeFrame_WithFileAndLine_ParsesAllParts()
    {
        var lines = StackTraceParser.Parse(
            @"at Demo.OrderService.Process(Int32 id) in C:\src\OrderService.cs:line 42");
        var f = Assert.Single(lines);
        Assert.False(f.IsExceptionHeader);
        Assert.Equal("Demo.OrderService", f.TypeFullName);
        Assert.Equal("Process", f.MethodName);
        Assert.Equal("Int32 id", f.Parameters);
        Assert.Equal(@"C:\src\OrderService.cs", f.File);
        Assert.Equal(42, f.Line);
    }

    [Fact]
    public void RuntimeFrame_WithoutFileInfo_ParsesTypeAndMethod()
    {
        var f = Assert.Single(StackTraceParser.Parse("   at Demo.OrderService.Process(Int32 id)"));
        Assert.Equal("Demo.OrderService", f.TypeFullName);
        Assert.Null(f.File);
        Assert.Null(f.Line);
    }

    [Fact]
    public void LogPrefixedFrame_AnchorsOnAt()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "2026-07-19 06:12:01.123 +02:00 [ERR]    at Demo.OrderService.Process(Int32 id)"));
        Assert.Equal("Demo.OrderService", f.TypeFullName);
        Assert.Equal("Process", f.MethodName);
    }

    [Fact]
    public void ExceptionHeader_AndInnerChain_AreRecognized()
    {
        var lines = StackTraceParser.Parse("""
            System.InvalidOperationException: boom
             ---> System.ArgumentNullException: Value cannot be null. (Parameter 'id')
               at Demo.OrderService.Process(Int32 id)
               --- End of inner exception stack trace ---
               at Demo.Program.Main()
            """);
        Assert.Equal(4, lines.Count);          // separator line dropped
        Assert.True(lines[0].IsExceptionHeader);
        Assert.Equal("System.InvalidOperationException", lines[0].TypeFullName);
        Assert.True(lines[1].IsExceptionHeader);
        Assert.Equal("System.ArgumentNullException", lines[1].TypeFullName);
        Assert.False(lines[2].IsExceptionHeader);
    }

    [Fact]
    public void MangledFrames_SplitOnLastDot_TypeKeepsMangledSegment()
    {
        var sm = Assert.Single(StackTraceParser.Parse(
            "at Demo.OrderService+<ProcessAsync>d__12.MoveNext()"));
        Assert.Equal("Demo.OrderService+<ProcessAsync>d__12", sm.TypeFullName);
        Assert.Equal("MoveNext", sm.MethodName);

        var lambda = Assert.Single(StackTraceParser.Parse(
            "at Demo.OrderService+<>c.<Process>b__5_0(Int32 x)"));
        Assert.Equal("Demo.OrderService+<>c", lambda.TypeFullName);
        Assert.Equal("<Process>b__5_0", lambda.MethodName);

        var local = Assert.Single(StackTraceParser.Parse(
            "at Demo.OrderService.<Process>g__Validate|5_0(Int32 x)"));
        Assert.Equal("Demo.OrderService", local.TypeFullName);
        Assert.Equal("<Process>g__Validate|5_0", local.MethodName);
    }

    [Fact]
    public void GenericTypeAndMethod_ParsesWithArityMarkers()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "at Demo.Repository`1.GetById[TKey](TKey key)"));
        Assert.Equal("Demo.Repository`1", f.TypeFullName);
        Assert.Equal("GetById", f.MethodName);      // [TKey] stripped from the name
    }

    [Fact]
    public void DemystifiedFrame_IsRecognized_AndMarkedAsync()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "at async Task<int> Demo.OrderService.ProcessAsync(int id)"));
        Assert.True(f.IsDemystified);
        Assert.True(f.DemystifiedAsync);
        Assert.Equal("Demo.OrderService", f.TypeFullName);
        Assert.Equal("ProcessAsync", f.MethodName);
    }

    [Fact]
    public void NoiseLines_AreDropped_SeparatorsAreDropped()
    {
        var lines = StackTraceParser.Parse("""
            some log chatter without a frame
            --- End of stack trace from previous location ---
            at Demo.Program.Main()
            """);
        var f = Assert.Single(lines);
        Assert.Equal("Main", f.MethodName);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
        => Assert.Empty(StackTraceParser.Parse("   \n\n  "));

    [Fact]
    public void Constructor_Frame_Parses()
    {
        var f = Assert.Single(StackTraceParser.Parse("at Demo.OrderService..ctor(String name)"));
        Assert.Equal("Demo.OrderService", f.TypeFullName);
        Assert.Equal(".ctor", f.MethodName);
    }
}
```

**Step 2: Run to verify failure**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~StackTraceParser"`
Expected: compile FAIL — `StackTraceParser` doesn't exist. Correct failure.

**Step 3: Implement**

`src/RoslynCodeLens/StackTrace/StackTraceParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace RoslynCodeLens.StackTrace;

/// <summary>One recognized line of a pasted stack trace, before demangling/resolution.</summary>
public sealed record ParsedTraceLine(
    string Raw,
    bool IsExceptionHeader,
    string TypeFullName,      // exception type for headers; declaring type (runtime-mangled form) for frames
    string MethodName,        // empty for headers; mangled method segment for frames; [T] args stripped
    string? Parameters,       // raw parameter list text, null for headers
    string? File,
    int? Line,
    bool IsDemystified,
    bool DemystifiedAsync);

public static partial class StackTraceParser
{
    // "at " frame anchor anywhere in the line (log prefixes come before it).
    [GeneratedRegex(@"(?:^|\s)at\s+(?<rest>.+)$")]
    private static partial Regex AtAnchor();

    // Runtime frame body: Method-part(params)[ in file:line N]
    [GeneratedRegex(@"^(?<method>[^\s(][^(]*)\((?<params>[^)]*)\)(?:\s+in\s+(?<file>.+?):line\s+(?<line>\d+))?\s*$")]
    private static partial Regex RuntimeFrame();

    // Exception header: [---> ]Fully.Qualified.TypeName: message   (type must contain a dot,
    // no spaces — keeps "ERROR: something" log lines out).
    [GeneratedRegex(@"^(?:--->\s+)?(?<type>[A-Za-z_][A-Za-z0-9_.+`]*\.[A-Za-z0-9_.+`]+)\s*:\s+.+$")]
    private static partial Regex ExceptionHeader();

    public static IReadOnlyList<ParsedTraceLine> Parse(string text)
    {
        var results = new List<ParsedTraceLine>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("---", StringComparison.Ordinal) &&
                line.Contains("stack trace", StringComparison.OrdinalIgnoreCase))
                continue; // "--- End of ... stack trace ---" separators

            var at = AtAnchor().Match(line);
            if (at.Success && TryParseFrame(line, at.Groups["rest"].Value, out var frame))
            {
                results.Add(frame);
                continue;
            }

            var header = ExceptionHeader().Match(line);
            if (header.Success && !line.Contains("):", StringComparison.Ordinal))
            {
                results.Add(new ParsedTraceLine(
                    line, IsExceptionHeader: true, header.Groups["type"].Value,
                    MethodName: "", Parameters: null, File: null, Line: null,
                    IsDemystified: false, DemystifiedAsync: false));
            }
            // else: noise — dropped
        }
        return results;
    }

    private static bool TryParseFrame(string raw, string body, out ParsedTraceLine frame)
    {
        frame = null!;
        var m = RuntimeFrame().Match(body);
        if (!m.Success) return false;

        var methodPart = m.Groups["method"].Value.Trim();
        var isDemystified = false;
        var demystifiedAsync = false;

        // Demystifier form: "[async ][static ]ReturnType Ns.Type.Method" — the part before
        // '(' contains spaces separating modifiers/return type from the method path.
        var lastSpace = methodPart.LastIndexOf(' ');
        if (lastSpace >= 0)
        {
            isDemystified = true;
            demystifiedAsync = methodPart.Contains("async ", StringComparison.Ordinal);
            methodPart = methodPart[(lastSpace + 1)..];
        }

        // Strip method generic args: GetById[TKey] -> GetById
        var bracket = methodPart.IndexOf('[');
        if (bracket > 0 && methodPart.EndsWith("]", StringComparison.Ordinal))
            methodPart = methodPart[..bracket];

        // Split type / method on the last '.' — but ".ctor"/".cctor" keep their leading dot.
        var splitAt = methodPart.EndsWith("..ctor", StringComparison.Ordinal) ? methodPart.Length - 5 - 1
            : methodPart.EndsWith("..cctor", StringComparison.Ordinal) ? methodPart.Length - 6 - 1
            : methodPart.LastIndexOf('.');
        if (splitAt <= 0 || splitAt == methodPart.Length - 1) return false;

        var type = methodPart[..splitAt];
        var method = methodPart[(splitAt + 1)..];

        frame = new ParsedTraceLine(
            raw, IsExceptionHeader: false, type, method,
            m.Groups["params"].Value,
            m.Groups["file"].Success ? m.Groups["file"].Value : null,
            m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : null,
            isDemystified, demystifiedAsync);
        return true;
    }
}
```

Adapt regex/`GeneratedRegex` usage to the project's C# version if needed (the repo targets net10.0 — `[GeneratedRegex]` on `static partial` methods works; if analyzers complain, plain `new Regex(..., RegexOptions.Compiled)` static fields are fine). The `..ctor` split math: verify with the Task 2 ctor test and fix constants if off-by-one — the intent is `type = "Demo.OrderService"`, `method = ".ctor"`.

**Step 4: Run to green**

Run: `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~StackTraceParser"`
Expected: PASS (all 10). Iterate on the regexes until the matrix is green — do not weaken tests.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/StackTrace/StackTraceParser.cs tests/RoslynCodeLens.Tests/StackTraceParserTests.cs
git commit -m "feat: stack trace line parser (runtime, log-prefixed, demystified, exception chains)"
```

---

### Task 3: StackFrameDemangler (TDD)

Pure mapping from parsed (type, method) to a logical target. No Roslyn.

**Files:**
- Create: `tests/RoslynCodeLens.Tests/StackFrameDemanglerTests.cs`
- Create: `src/RoslynCodeLens/StackTrace/StackFrameDemangler.cs`

**Step 1: Write the failing tests**

```csharp
using RoslynCodeLens.StackTrace;

namespace RoslynCodeLens.Tests;

public class StackFrameDemanglerTests
{
    private static DemangledTarget D(string type, string method)
        => StackFrameDemangler.Demangle(type, method);

    [Fact]
    public void StateMachine_MapsToLogicalMethod()
    {
        var d = D("Demo.OrderService+<ProcessAsync>d__12", "MoveNext");
        Assert.Equal("Demo.OrderService", d.TypeName);
        Assert.Equal("ProcessAsync", d.MethodName);
        Assert.Equal(DemangledKind.StateMachine, d.Kind);
        Assert.Null(d.EnclosingMethod);
    }

    [Fact]
    public void LambdaInSharedContainer_MapsToEnclosingMethod()
    {
        var d = D("Demo.OrderService+<>c", "<Process>b__5_0");
        Assert.Equal("Demo.OrderService", d.TypeName);
        Assert.Equal("Process", d.MethodName);
        Assert.Equal(DemangledKind.Lambda, d.Kind);
        Assert.Equal("Process", d.EnclosingMethod);
    }

    [Fact]
    public void LambdaInDisplayClass_MapsToEnclosingMethod()
    {
        var d = D("Demo.OrderService+<>c__DisplayClass5_0", "<Process>b__1");
        Assert.Equal("Demo.OrderService", d.TypeName);
        Assert.Equal("Process", d.MethodName);
        Assert.Equal(DemangledKind.Lambda, d.Kind);
    }

    [Fact]
    public void LocalFunction_MapsToNameAndEnclosing()
    {
        var d = D("Demo.OrderService", "<Process>g__Validate|5_0");
        Assert.Equal("Demo.OrderService", d.TypeName);
        Assert.Equal("Process", d.MethodName);       // resolve against the enclosing method
        Assert.Equal(DemangledKind.LocalFunction, d.Kind);
        Assert.Equal("Validate", d.LocalFunctionName);
    }

    [Fact]
    public void Constructor_IsMarked()
    {
        var d = D("Demo.OrderService", ".ctor");
        Assert.Equal(DemangledKind.Constructor, d.Kind);
        Assert.Equal("Demo.OrderService", d.TypeName);
    }

    [Fact]
    public void GenericArity_AndNesting_AreNormalized()
    {
        var d = D("Demo.Repository`1+Enumerator", "MoveNext");
        Assert.Equal("Demo.Repository.Enumerator", d.TypeName);   // '+'->'.', `1 stripped
        Assert.Equal(DemangledKind.Plain, d.Kind);                // MoveNext on a real nested type is NOT a state machine
    }

    [Fact]
    public void PlainFrame_PassesThrough()
    {
        var d = D("Demo.OrderService", "Process");
        Assert.Equal(DemangledKind.Plain, d.Kind);
        Assert.Equal("Process", d.MethodName);
    }
}
```

**Step 2: Verify failure** — compile error, `StackFrameDemangler` missing.

**Step 3: Implement**

```csharp
using System.Text.RegularExpressions;

namespace RoslynCodeLens.StackTrace;

public enum DemangledKind { Plain, StateMachine, Lambda, LocalFunction, Constructor }

/// <summary>Logical target of a (possibly compiler-mangled) stack frame. TypeName is
/// normalized to display form: '+' nesting becomes '.', backtick arity is stripped
/// (the resolver's arity-stripped index matches it).</summary>
public sealed record DemangledTarget(
    string TypeName, string MethodName, DemangledKind Kind,
    string? EnclosingMethod, string? LocalFunctionName);

public static partial class StackFrameDemangler
{
    [GeneratedRegex(@"^<(?<m>[^>]+)>d__\d+$")] private static partial Regex StateMachineSegment();
    [GeneratedRegex(@"^<>c(__DisplayClass[\d_]+)?$")] private static partial Regex LambdaContainer();
    [GeneratedRegex(@"^<(?<m>[^>]+)>b__[\d_]+$")] private static partial Regex LambdaMethod();
    [GeneratedRegex(@"^<(?<m>[^>]+)>g__(?<name>[^|]+)\|[\d_]+$")] private static partial Regex LocalFunctionMethod();
    [GeneratedRegex(@"`\d+")] private static partial Regex Arity();

    public static DemangledTarget Demangle(string typeFullName, string methodName)
    {
        var segments = typeFullName.Split('+');
        var last = segments[^1];

        // async/iterator state machine: Ns.T+<M>d__N.MoveNext
        var sm = StateMachineSegment().Match(last);
        if (sm.Success && methodName is "MoveNext")
        {
            return new DemangledTarget(
                Normalize(segments[..^1]), sm.Groups["m"].Value,
                DemangledKind.StateMachine, null, null);
        }

        // lambda containers: Ns.T+<>c.<M>b__N / Ns.T+<>c__DisplayClassN_M.<M>b__K
        if (LambdaContainer().IsMatch(last))
        {
            var lm = LambdaMethod().Match(methodName);
            if (lm.Success)
            {
                var m = lm.Groups["m"].Value;
                return new DemangledTarget(
                    Normalize(segments[..^1]), m, DemangledKind.Lambda, m, null);
            }
        }

        // lambda emitted directly on the user type (static lambdas without captures)
        var direct = LambdaMethod().Match(methodName);
        if (direct.Success)
        {
            var m = direct.Groups["m"].Value;
            return new DemangledTarget(Normalize(segments), m, DemangledKind.Lambda, m, null);
        }

        // local function: Ns.T.<M>g__Name|N_M
        var lf = LocalFunctionMethod().Match(methodName);
        if (lf.Success)
        {
            return new DemangledTarget(
                Normalize(segments), lf.Groups["m"].Value,
                DemangledKind.LocalFunction, lf.Groups["m"].Value, lf.Groups["name"].Value);
        }

        if (methodName is ".ctor" or ".cctor")
            return new DemangledTarget(Normalize(segments), methodName, DemangledKind.Constructor, null, null);

        return new DemangledTarget(Normalize(segments), methodName, DemangledKind.Plain, null, null);
    }

    private static string Normalize(string[] segments)
        => Arity().Replace(string.Join('.', segments), "");
}
```

**Step 4: Run to green** — `--filter "FullyQualifiedName~StackFrameDemangler"` → PASS (7).

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/StackTrace/StackFrameDemangler.cs tests/RoslynCodeLens.Tests/StackFrameDemanglerTests.cs
git commit -m "feat: stack frame demangler (state machines, lambdas, local functions, generics)"
```

---

### Task 4: ResolveStackTraceLogic (TDD)

**Files:**
- Create: `tests/RoslynCodeLens.Tests/ResolveStackTraceLogicTests.cs`
- Create: `src/RoslynCodeLens/Tools/ResolveStackTraceLogic.cs`

**Step 1: Write the failing tests** (uses `RenameTestWorkspace`; read `MetadataSymbolResolver.Resolve(string name)` and construct one the way `TestSolutionFixture` does: `new MetadataSymbolResolver(loaded, resolver)`)

```csharp
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class ResolveStackTraceLogicTests
{
    private const string SourceText = """
        namespace Demo;
        public class OrderService
        {
            public OrderService(string name) { }
            public int Process(int id)
            {
                int Validate(int x) => x;
                var f = new System.Func<int, int>(y => y);
                return Validate(f(id));
            }
            public async System.Threading.Tasks.Task<int> ProcessAsync(int id)
            {
                await System.Threading.Tasks.Task.Yield();
                return id;
            }
        }
        public class Repository<T>
        {
            public T? GetById(int key) => default;
        }
        """;

    private static IReadOnlyList<StackFrameInfo> Resolve(string trace)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Demo.cs", SourceText));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        return ResolveStackTraceLogic.Execute(loaded, resolver, metadata, trace);
    }

    [Fact]
    public void AsyncStateMachineFrame_ResolvesToSourceMethod_KindAsync()
    {
        var frames = Resolve("at Demo.OrderService+<ProcessAsync>d__2.MoveNext()");
        var f = Assert.Single(frames);
        Assert.Equal("asyncMethod", f.Kind);
        Assert.Equal("source", f.Origin);
        Assert.Contains("ProcessAsync", f.Symbol, StringComparison.Ordinal);
        Assert.Equal("Demo.cs", f.File);
        Assert.NotNull(f.Line);
    }

    [Fact]
    public void LambdaFrame_ResolvesToEnclosingMethod()
    {
        var f = Assert.Single(Resolve("at Demo.OrderService+<>c.<Process>b__1_0(Int32 y)"));
        Assert.Equal("lambda", f.Kind);
        Assert.Equal("Process", f.EnclosingMethod);
        Assert.Equal("source", f.Origin);
    }

    [Fact]
    public void LocalFunctionFrame_ResolvesToEnclosingMethod()
    {
        var f = Assert.Single(Resolve("at Demo.OrderService.<Process>g__Validate|1_0(Int32 x)"));
        Assert.Equal("localFunction", f.Kind);
        Assert.Equal("Process", f.EnclosingMethod);
        Assert.Equal("source", f.Origin);
    }

    [Fact]
    public void GenericTypeFrame_ResolvesViaStrippedArity()
    {
        var f = Assert.Single(Resolve("at Demo.Repository`1.GetById(Int32 key)"));
        Assert.Equal("source", f.Origin);
        Assert.Contains("GetById", f.Symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorFrame_Resolves()
    {
        var f = Assert.Single(Resolve("at Demo.OrderService..ctor(String name)"));
        Assert.Equal("constructor", f.Kind);
        Assert.Equal("source", f.Origin);
    }

    [Fact]
    public void MetadataFrame_ResolvesWithMetadataOrigin_NoLocation()
    {
        var f = Assert.Single(Resolve("at System.String.Concat(String str0, String str1)"));
        Assert.Equal("metadata", f.Origin);
        Assert.Null(f.File);
    }

    [Fact]
    public void UnknownFrame_ComesBackUnresolved_WithParsedSymbol()
    {
        var f = Assert.Single(Resolve("at Vendor.Thing.DoIt()"));
        Assert.Equal("unresolved", f.Origin);
        Assert.Contains("Vendor.Thing.DoIt", f.Symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameWithExplicitFileLine_KeepsExactLocation()
    {
        var f = Assert.Single(Resolve(
            @"at Demo.OrderService.Process(Int32 id) in C:\real\Demo.cs:line 99"));
        Assert.Equal(@"C:\real\Demo.cs", f.File);
        Assert.Equal(99, f.Line);
        Assert.Equal("source", f.Origin);
    }

    [Fact]
    public void ExceptionHeaders_BecomeExceptionItems_InOrder()
    {
        var frames = Resolve("""
            System.InvalidOperationException: boom
               at Demo.OrderService.Process(Int32 id)
            """);
        Assert.Equal(2, frames.Count);
        Assert.Equal("exception", frames[0].Kind);
        Assert.Equal(0, frames[0].Index);
        Assert.Equal(1, frames[1].Index);
    }

    [Fact]
    public void EmptyOrNoiseOnlyInput_ThrowsInvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Resolve("no frames here at all"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }
}
```

Note the lambda/local-function suffix numbers (`b__1_0`, `g__Validate|1_0`): the numeric suffixes are arbitrary in tests — the demangler ignores them — but the *method name inside `<>`* must match a real method (`Process`).

**Step 2: Verify failure** — compile error, logic missing.

**Step 3: Implement**

`src/RoslynCodeLens/Tools/ResolveStackTraceLogic.cs`:

```csharp
using Microsoft.CodeAnalysis;
using RoslynCodeLens.Models;
using RoslynCodeLens.StackTrace;
using RoslynCodeLens.Symbols;

namespace RoslynCodeLens.Tools;

public static class ResolveStackTraceLogic
{
    public static IReadOnlyList<StackFrameInfo> Execute(
        LoadedSolution loaded, SymbolResolver resolver, MetadataSymbolResolver metadata, string stackTrace)
    {
        var parsed = StackTraceParser.Parse(stackTrace);
        if (parsed.Count == 0)
        {
            throw new McpToolException(ToolErrorCode.InvalidArgument,
                "No stack frames recognized in input.", new { });
        }

        var frames = new List<StackFrameInfo>(parsed.Count);
        foreach (var line in parsed)
        {
            frames.Add(line.IsExceptionHeader
                ? ResolveException(resolver, metadata, line, frames.Count)
                : ResolveFrame(resolver, metadata, line, frames.Count));
        }
        return frames;
    }

    private static StackFrameInfo ResolveException(
        SymbolResolver resolver, MetadataSymbolResolver metadata, ParsedTraceLine line, int index)
    {
        var typeName = line.TypeFullName.Replace('+', '.');
        var type = resolver.FindSymbols(typeName).FirstOrDefault() ?? metadata.Resolve(typeName)?.Symbol;
        var (origin, file, srcLine, project) = Locate(resolver, type);
        return new StackFrameInfo(index, line.Raw, "exception", typeName,
            null, file, srcLine, origin, project);
    }

    private static StackFrameInfo ResolveFrame(
        SymbolResolver resolver, MetadataSymbolResolver metadata, ParsedTraceLine line, int index)
    {
        var target = line.IsDemystified
            ? new DemangledTarget(line.TypeFullName.Replace('+', '.'), line.MethodName,
                line.DemystifiedAsync ? DemangledKind.StateMachine : DemangledKind.Plain, null, null)
            : StackFrameDemangler.Demangle(line.TypeFullName, line.MethodName);

        var method = ResolveMethod(resolver, metadata, target, line.Parameters);
        var kind = KindOf(target, method);
        var symbol = method?.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)
                     ?? $"{target.TypeName}.{target.MethodName}";

        var (origin, file, srcLine, project) = Locate(resolver, method);
        // A trace-supplied location is exact — it wins over the declaration site.
        if (line.File != null) { file = line.File; srcLine = line.Line; }

        return new StackFrameInfo(index, line.Raw, kind, symbol,
            target.Kind is DemangledKind.Lambda or DemangledKind.LocalFunction ? target.EnclosingMethod : null,
            file, srcLine, origin, project);
    }

    private static ISymbol? ResolveMethod(
        SymbolResolver resolver, MetadataSymbolResolver metadata, DemangledTarget target, string? parameters)
    {
        // Constructor: resolve the type, pick a ctor by param count.
        var memberName = target.Kind == DemangledKind.Constructor ? target.TypeName.Split('.')[^1] : target.MethodName;
        var candidates = resolver.FindSymbols($"{target.TypeName}.{memberName}");
        if (target.Kind == DemangledKind.Constructor)
        {
            var type = resolver.FindSymbols(target.TypeName).OfType<INamedTypeSymbol>().FirstOrDefault();
            candidates = type?.InstanceConstructors.Cast<ISymbol>().ToList() ?? [];
        }

        if (candidates.Count == 0)
            return metadata.Resolve($"{target.TypeName}.{target.MethodName}")?.Symbol
                ?? metadata.Resolve(target.TypeName)?.Symbol;

        if (candidates.Count == 1) return candidates[0];

        // Overloads: prefer matching parameter count from the parsed parameter list.
        var paramCount = string.IsNullOrWhiteSpace(parameters)
            ? 0 : parameters!.Split(',').Length;
        return candidates.OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == paramCount)
            ?? candidates[0];
    }

    private static string KindOf(DemangledTarget target, ISymbol? method) => target.Kind switch
    {
        DemangledKind.Lambda => "lambda",
        DemangledKind.LocalFunction => "localFunction",
        DemangledKind.Constructor => "constructor",
        DemangledKind.StateMachine when method is IMethodSymbol ms =>
            ms.IsAsync || ms.ReturnType.Name is "Task" or "ValueTask" ? "asyncMethod"
            : ms.ReturnType.Name.StartsWith("IEnumera", StringComparison.Ordinal)
              || ms.ReturnType.Name.StartsWith("IAsyncEnumera", StringComparison.Ordinal) ? "iterator"
            : "method",
        DemangledKind.StateMachine => "unknown",
        _ => method == null ? "unknown" : "method",
    };

    private static (string Origin, string? File, int? Line, string? Project) Locate(
        SymbolResolver resolver, ISymbol? symbol)
    {
        if (symbol == null) return ("unresolved", null, null, null);
        if (!symbol.Locations.Any(l => l.IsInSource)) return ("metadata", null, null, null);
        var (file, line) = resolver.GetFileAndLine(symbol);
        return ("source", file, line, resolver.GetProjectName(symbol));
    }
}
```

Check real API shapes while implementing (this is reference code, not gospel): `MetadataSymbolResolver.Resolve` semantics for member strings, `SymbolDisplayFormat` choice (any short format is fine — pin whatever you pick in the tests via `Contains`, already done above), `GetProjectName` overloads.

**Step 4: Run to green** — `--filter "FullyQualifiedName~ResolveStackTrace"` → PASS (11). Debug root causes (likely: metadata member resolution shape, ctor lookup) rather than weakening asserts.

**Step 5: Commit**

```bash
git add src/RoslynCodeLens/Tools/ResolveStackTraceLogic.cs tests/RoslynCodeLens.Tests/ResolveStackTraceLogicTests.cs
git commit -m "feat: resolve_stack_trace resolution logic over the symbol resolvers"
```

---

### Task 5: Tool wrapper + fixture integration test

**Files:**
- Create: `src/RoslynCodeLens/Tools/ResolveStackTraceTool.cs`
- Create: `tests/RoslynCodeLens.Tests/ResolveStackTraceFixtureTests.cs`

**Step 1: Wrapper** (copy `FindReferencesTool`'s envelope pattern; verify `GetAnalysisContext()` member names — `context.Loaded`, `context.Resolver`, and the metadata resolver member — in `SolutionAnalysisContext.cs`)

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ResolveStackTraceTool
{
    private const int DefaultLimit = 500;

    [McpServerTool(Name = "resolve_stack_trace"),
     Description("Map a pasted .NET stack trace to file/line/symbol against the loaded solution, " +
                 "undoing compiler name mangling: async/iterator state machines (<M>d__N.MoveNext), " +
                 "lambdas (<>c / <>c__DisplayClass), local functions (g__Name|), generic arity. " +
                 "Handles Exception.ToString() output, log-prefixed lines, inner-exception chains, and " +
                 "Ben.Demystifier-style traces. Frames without 'in file:line' get the declaration site; " +
                 "frames with it keep the exact location. External frames resolve with origin=metadata. " +
                 "Items are in original trace order.")]
    public static ToolListResult<StackFrameInfo> Execute(
        MultiSolutionManager manager,
        [Description("The stack trace text, pasted as-is (multi-line)")] string stackTrace,
        [Description("Maximum number of items to return (default: 500)")] int? limit = null)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        var frames = ResolveStackTraceLogic.Execute(
            context.Loaded, context.Resolver, context.Metadata, stackTrace);

        var summary = new
        {
            byOrigin = new
            {
                source = frames.Count(f => f.Origin == "source"),
                metadata = frames.Count(f => f.Origin == "metadata"),
                unresolved = frames.Count(f => f.Origin == "unresolved"),
            },
            exceptions = frames.Count(f => f.Kind == "exception"),
        };
        return ToolListResult.Create(frames, limit ?? DefaultLimit, summary);
    }
}
```

**Step 2: Fixture integration test** — one realistic trace against the shared TestSolution (read-only; `Greeter.Greet` lives in TestLib):

```csharp
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

[Collection("TestSolution")]
public class ResolveStackTraceFixtureTests
{
    private readonly TestSolutionFixture _fixture;
    public ResolveStackTraceFixtureTests(TestSolutionFixture fixture) => _fixture = fixture;

    [Fact]
    public void RealisticTrace_ResolvesSourceAndMetadataFrames_InOrder()
    {
        var frames = ResolveStackTraceLogic.Execute(
            _fixture.Loaded, _fixture.Resolver, _fixture.Metadata, """
            System.InvalidOperationException: boom
               at System.String.Concat(String str0, String str1)
               at TestLib.Greeter.Greet(String name)
               --- End of stack trace from previous location ---
            random log noise
            """);

        Assert.Equal(3, frames.Count);   // header + 2 frames; separator + noise dropped
        Assert.Equal("exception", frames[0].Kind);
        Assert.Equal("metadata", frames[1].Origin);
        var greet = frames[2];
        Assert.Equal("source", greet.Origin);
        Assert.EndsWith("Greeter.cs", greet.File!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TestLib", greet.Project);
    }
}
```

Adjust the `Greet` signature in the trace line to the fixture's real one (Read `tests/RoslynCodeLens.Tests/Fixtures/TestSolution/TestLib/Greeter.cs` first).

**Step 3: Run**

`dotnet build src/RoslynCodeLens` → 0 errors; `dotnet test tests/RoslynCodeLens.Tests --filter "FullyQualifiedName~ResolveStackTrace"` → PASS (12).

**Step 4: Commit**

```bash
git add src/RoslynCodeLens/Tools/ResolveStackTraceTool.cs tests/RoslynCodeLens.Tests/ResolveStackTraceFixtureTests.cs
git commit -m "feat: expose resolve_stack_trace MCP tool"
```

---

### Task 6: Docs + full verification

**Files:**
- Modify: `plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md`
- Modify: `CLAUDE.md` (58 → 59 tools)
- Modify: `docs/BACKLOG.md`

**Step 1: SKILL.md** (match existing voice; find sections by heading):
1. Red Flags row: `| "Where did this exception come from?" / pasting a stack trace / "What line is <M>d__12.MoveNext?" | \`resolve_stack_trace\` |`
2. "Understanding a Codebase"/Diagnostics area bullet: `- \`resolve_stack_trace\` — map a pasted stack trace to file/line/symbol; demangles async state machines, lambdas, local functions; source frames get declaration site (exact when the trace carries in file:line), external frames origin="metadata", items in trace order.`
3. Quick Reference row: `| \`resolve_stack_trace\` | "Where did this exception come from?" / "Resolve this stack trace" |`
4. Metadata-support table row: `| \`resolve_stack_trace\` | Yes — frames resolve to source or metadata symbols | Metadata frames have no file/line | \`peek_il\` to inspect external method bodies |`

**Step 2: CLAUDE.md** — "58 code intelligence tools" → "59 code intelligence tools".

**Step 3: BACKLOG.md** — §5 High value: mark the `resolve_stack_trace` bullet ✅ *shipped* with design-doc link (same style as the rename_symbol bullet); on merge move a row into Recently shipped (`resolve_stack_trace` | Navigation | #<PR>). Add nothing to In flight if PR is opened immediately.

**Step 4: Full verification**

Run: `dotnet build` → 0 errors. Run: `dotnet test` → all pass (expect ~697+: 669 + ~28 new). `git status --short tests/RoslynCodeLens.Tests/Fixtures/` → empty.

**Step 5: Commit**

```bash
git add plugins/roslyn-codelens/skills/roslyn-codelens/SKILL.md CLAUDE.md docs/BACKLOG.md
git commit -m "docs: document resolve_stack_trace (59 tools)"
```

Then superpowers:verification-before-completion → superpowers:finishing-a-development-branch (PR to `main`).

---

## Deviations

Note any deviation (API mismatch, regex adaptation, metadata-resolution shape) in the final report; design-relevant ones get appended to the design doc.
