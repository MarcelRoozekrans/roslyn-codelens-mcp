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
            public System.Collections.Generic.IEnumerable<int> Numbers(int count)
            {
                for (var i = 0; i < count; i++)
                    yield return i;
            }
        }
        public class Repository<T>
        {
            public T? GetById(int key) => default;
        }
        """;

    // Global-namespace Program matching the empirical modern .NET trace fixtures.
    private const string ProgramSource = """
        public class Program
        {
            public static async System.Threading.Tasks.Task Case1_AsyncLambda()
            {
                var f = new System.Func<System.Threading.Tasks.Task>(async () =>
                {
                    await System.Threading.Tasks.Task.Yield();
                });
                await f();
            }
            public static void Case2b_CapturingLocalFunctionWithLambda()
            {
                var captured = 0;
                void Boom() => captured++;
                var f = new System.Func<int>(() => captured);
                Boom();
                f();
            }
        }
        """;

    // Line numbers in this file are asserted below — keep declarations on their lines.
    private const string OverloadSource = """
        namespace Demo;
        public class Dispatcher
        {
            public void Handle(System.Collections.Generic.Dictionary<string, int> map) { }
            public void Handle(string a, int b) { }
            public void Save(System.Collections.Generic.Dictionary<string, int> map) { }
            public void Save(string a, int b) { }
        }
        public class Config
        {
            public Config() { }
            static Config() { }
        }
        public class Repository<TKey, TValue>
        {
            public TValue? GetById(TKey key) => default!;
        }
        """;

    private const int HandleDictionaryLine = 4;
    private const int SaveDictionaryLine = 6;
    private const int InstanceCtorLine = 11;
    private const int StaticCtorLine = 12;

    private static IReadOnlyList<StackFrameInfo> Resolve(string trace)
        => ResolveFull(trace).Frames;

    private static StackTraceResolution ResolveFull(string trace)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Demo.cs", SourceText), ("Program.cs", ProgramSource), ("Overloads.cs", OverloadSource));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        return ResolveStackTraceLogic.Execute(resolver, metadata, trace);
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
    public void IteratorStateMachineFrame_ResolvesToSourceMethod_KindIterator()
    {
        var f = Assert.Single(Resolve("at Demo.OrderService+<Numbers>d__3.MoveNext()"));
        Assert.Equal("iterator", f.Kind);
        Assert.Equal("source", f.Origin);
        Assert.Contains("Numbers", f.Symbol, StringComparison.Ordinal);
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
    public void LambdaFrame_DisplayClassForm_ResolvesToEnclosingMethod()
    {
        var f = Assert.Single(Resolve("at Demo.OrderService+<>c__DisplayClass1_0.<Process>b__1(Int32 y)"));
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

    // ---- item 8: multi-line noise still throws (fallback is single-line only) ----

    [Fact]
    public void MultiLineNoiseOnlyInput_ThrowsInvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Resolve("noise line one\nnoise line two"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    // ---- items 1/2: empirical dot-nested modern .NET frames resolve to source ----

    [Fact]
    public void Empirical_AsyncLambdaFrame_DotNested_ResolvesToSource()
    {
        var f = Assert.Single(Resolve("at Program.<>c.<<Case1_AsyncLambda>b__8_0>d.MoveNext()"));
        Assert.Equal("lambda", f.Kind);
        Assert.Equal("Case1_AsyncLambda", f.EnclosingMethod);
        Assert.Equal("source", f.Origin);
        Assert.Equal("Program.cs", f.File);
    }

    [Fact]
    public void Empirical_LocalFunctionFrame_DotNestedDisplayClass_ResolvesToSource()
    {
        var f = Assert.Single(Resolve(
            "at Program.<>c__DisplayClass10_0.<Case2b_CapturingLocalFunctionWithLambda>g__Boom|1()"));
        Assert.Equal("localFunction", f.Kind);
        Assert.Equal("Case2b_CapturingLocalFunctionWithLambda", f.EnclosingMethod);
        Assert.Equal("source", f.Origin);
        Assert.Equal("Program.cs", f.File);
    }

    // ---- items 4/5: AOT offset frames parse; garbage frame-like lines interleave ----

    [Fact]
    public void FrameLikeGarbage_EmitsUnknownItem_InOrder_AndIsCounted()
    {
        var result = ResolveFull("""
            System.InvalidOperationException: boom
               at ???bogus
               at MyApp.Foo.Bar() + 0x39
            """);
        Assert.Equal(3, result.Frames.Count);
        Assert.Equal(1, result.SkippedFrameLike);

        var unknown = result.Frames[1];
        Assert.Equal(1, unknown.Index);
        Assert.Equal("unknown", unknown.Kind);
        Assert.Equal("unresolved", unknown.Origin);
        Assert.Equal("at ???bogus", unknown.Symbol);
        Assert.Null(unknown.File);
        Assert.Null(unknown.Line);

        // The AOT frame now parses (offset tolerated) instead of being dropped.
        var aot = result.Frames[2];
        Assert.Equal(2, aot.Index);
        Assert.Contains("MyApp.Foo", aot.Symbol, StringComparison.Ordinal);
    }

    // ---- item 9: overload pick counts only top-level commas ----

    [Fact]
    public void OverloadPick_RuntimeGenericInstantiationCommas_DoNotCountAsParameters()
    {
        var f = Assert.Single(Resolve(
            "at Demo.Dispatcher.Handle(System.Collections.Generic.Dictionary`2[System.String,System.Int32] map)"));
        Assert.Equal("source", f.Origin);
        Assert.Equal(HandleDictionaryLine, f.Line); // 1-arg Dictionary overload, not Handle(string, int)
    }

    [Fact]
    public void OverloadPick_DemystifiedGenericAngleCommas_DoNotCountAsParameters()
    {
        var f = Assert.Single(Resolve("at void Demo.Dispatcher.Save(Dictionary<string, int> map)"));
        Assert.Equal("source", f.Origin);
        Assert.Equal(SaveDictionaryLine, f.Line); // 1-arg Dictionary overload, not Save(string, int)
    }

    // ---- item 10: '.cctor' resolves to the static constructor ----

    [Fact]
    public void StaticConstructorFrame_ResolvesToStaticCtorDeclaration()
    {
        var f = Assert.Single(Resolve("at Demo.Config..cctor()"));
        Assert.Equal("constructor", f.Kind);
        Assert.Equal("source", f.Origin);
        Assert.Equal(StaticCtorLine, f.Line);
    }

    [Fact]
    public void InstanceConstructorFrame_StillResolvesToInstanceCtorDeclaration()
    {
        var f = Assert.Single(Resolve("at Demo.Config..ctor()"));
        Assert.Equal("constructor", f.Kind);
        Assert.Equal("source", f.Origin);
        Assert.Equal(InstanceCtorLine, f.Line);
    }

    // ---- item 11: metadata/source name forms for generic types ----

    [Fact]
    public void DemystifiedGenericTypeFrame_WithSpacedTypeArgs_ResolvesToSource()
    {
        var f = Assert.Single(Resolve("at TValue Demo.Repository<TKey, TValue>.GetById(TKey key)"));
        Assert.Equal("source", f.Origin);
        Assert.Contains("GetById", f.Symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void DemystifiedGenericTypeFrame_WithoutSpaces_ResolvesViaAngleStrippedLookup()
    {
        var f = Assert.Single(Resolve("at TValue Demo.Repository<TKey,TValue>.GetById(TKey key)"));
        Assert.Equal("source", f.Origin);
        Assert.Contains("GetById", f.Symbol, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeGenericMetadataFrame_BacktickForm_ResolvesToMetadata()
    {
        var f = Assert.Single(Resolve("at System.Collections.Generic.List`1.Add(T item)"));
        Assert.Equal("metadata", f.Origin);
        Assert.Contains("Add", f.Symbol, StringComparison.Ordinal);
        Assert.Null(f.File);
    }
}
