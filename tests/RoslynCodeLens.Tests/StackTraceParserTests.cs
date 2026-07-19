using RoslynCodeLens.StackTrace;

namespace RoslynCodeLens.Tests;

public class StackTraceParserTests
{
    [Fact]
    public void RuntimeFrame_WithFileAndLine_ParsesAllParts()
    {
        var lines = StackTraceParser.Parse(
            @"at Demo.OrderService.Process(Int32 id) in C:\src\OrderService.cs:line 42").Lines;
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
        var f = Assert.Single(StackTraceParser.Parse("   at Demo.OrderService.Process(Int32 id)").Lines);
        Assert.Equal("Demo.OrderService", f.TypeFullName);
        Assert.Null(f.File);
        Assert.Null(f.Line);
    }

    [Fact]
    public void LogPrefixedFrame_AnchorsOnAt()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "2026-07-19 06:12:01.123 +02:00 [ERR]    at Demo.OrderService.Process(Int32 id)").Lines);
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
            """).Lines;
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
            "at Demo.OrderService+<ProcessAsync>d__12.MoveNext()").Lines);
        Assert.Equal("Demo.OrderService+<ProcessAsync>d__12", sm.TypeFullName);
        Assert.Equal("MoveNext", sm.MethodName);

        var lambda = Assert.Single(StackTraceParser.Parse(
            "at Demo.OrderService+<>c.<Process>b__5_0(Int32 x)").Lines);
        Assert.Equal("Demo.OrderService+<>c", lambda.TypeFullName);
        Assert.Equal("<Process>b__5_0", lambda.MethodName);

        var local = Assert.Single(StackTraceParser.Parse(
            "at Demo.OrderService.<Process>g__Validate|5_0(Int32 x)").Lines);
        Assert.Equal("Demo.OrderService", local.TypeFullName);
        Assert.Equal("<Process>g__Validate|5_0", local.MethodName);
    }

    [Fact]
    public void GenericTypeAndMethod_ParsesWithArityMarkers()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "at Demo.Repository`1.GetById[TKey](TKey key)").Lines);
        Assert.Equal("Demo.Repository`1", f.TypeFullName);
        Assert.Equal("GetById", f.MethodName);      // [TKey] stripped from the name
    }

    [Fact]
    public void DemystifiedFrame_IsRecognized_AndMarkedAsync()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "at async Task<int> Demo.OrderService.ProcessAsync(int id)").Lines);
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
            """).Lines;
        var f = Assert.Single(lines);
        Assert.Equal("Main", f.MethodName);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
        => Assert.Empty(StackTraceParser.Parse("   \n\n  ").Lines);

    [Fact]
    public void Constructor_Frame_Parses()
    {
        var f = Assert.Single(StackTraceParser.Parse("at Demo.OrderService..ctor(String name)").Lines);
        Assert.Equal("Demo.OrderService", f.TypeFullName);
        Assert.Equal(".ctor", f.MethodName);
    }

    // ---- item 1/2: empirical modern .NET trace lines (verbatim fixtures) ----

    [Fact]
    public void Empirical_ModernRuntimeLines_ParseWithDotNesting()
    {
        var lines = StackTraceParser.Parse("""
               at Program.<>c.<<Case1_AsyncLambda>b__8_0>d.MoveNext()
               at Program.<Case2_CapturingLocalFunction>g__Boom|9_0(<>c__DisplayClass9_0&)
               at Program.<>c__DisplayClass10_0.<Case2b_CapturingLocalFunctionWithLambda>g__Boom|1()
               at ThrowingStatics..cctor()
               at Program.GenericThrow[T](T x) in C:\app\Program.cs:line 71
            """).Lines;
        Assert.Equal(5, lines.Count);

        Assert.Equal("Program.<>c.<<Case1_AsyncLambda>b__8_0>d", lines[0].TypeFullName);
        Assert.Equal("MoveNext", lines[0].MethodName);

        Assert.Equal("Program", lines[1].TypeFullName);
        Assert.Equal("<Case2_CapturingLocalFunction>g__Boom|9_0", lines[1].MethodName);
        Assert.Equal("<>c__DisplayClass9_0&", lines[1].Parameters);

        Assert.Equal("Program.<>c__DisplayClass10_0", lines[2].TypeFullName);
        Assert.Equal("<Case2b_CapturingLocalFunctionWithLambda>g__Boom|1", lines[2].MethodName);

        Assert.Equal("ThrowingStatics", lines[3].TypeFullName);
        Assert.Equal(".cctor", lines[3].MethodName);

        Assert.Equal("Program", lines[4].TypeFullName);
        Assert.Equal("GenericThrow", lines[4].MethodName);
        Assert.Equal(71, lines[4].Line);
        Assert.False(lines[4].IsDemystified);
    }

    // ---- item 3: Demystifier grammar ----

    [Fact]
    public void Demystified_GenericReturnAndGenericType_KeepsFullMethodPath()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "at TValue Demo.Repository<TKey, TValue>.GetById(TKey key)").Lines);
        Assert.True(f.IsDemystified);
        Assert.Equal("Demo.Repository<TKey, TValue>", f.TypeFullName);
        Assert.Equal("GetById", f.MethodName);
        Assert.Equal("TKey key", f.Parameters);
    }

    [Fact]
    public void Demystified_TupleReturnType_DoesNotTruncateMethodPath()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "at (bool ok, int v) Demo.Svc.TryGet(int id)").Lines);
        Assert.True(f.IsDemystified);
        Assert.Equal("Demo.Svc", f.TypeFullName);
        Assert.Equal("TryGet", f.MethodName);
        Assert.Equal("int id", f.Parameters);
    }

    [Fact]
    public void Demystified_LocalFunctionSuffix_MapsToEnclosingMethod()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "at void Demo.Svc.M(String[] a)+LocalFunc(int x)").Lines);
        Assert.True(f.IsDemystified);
        Assert.Equal("Demo.Svc", f.TypeFullName);
        Assert.Equal("M", f.MethodName);
        Assert.Equal("LocalFunc", f.DemystifiedLocalFunction);
        Assert.Equal("String[] a", f.Parameters);
    }

    [Fact]
    public void Demystified_LambdaSuffix_IsMarked()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "at void Demo.Svc.M(String[] a)+(String s) => { }").Lines);
        Assert.True(f.IsDemystified);
        Assert.Equal("Demo.Svc", f.TypeFullName);
        Assert.Equal("M", f.MethodName);
        Assert.True(f.DemystifiedLambda);
    }

    // ---- item 4 (#303): AOT native offsets ----

    [Fact]
    public void AotFrame_WithHexOffset_Parses()
    {
        var f = Assert.Single(StackTraceParser.Parse("at MyApp.Foo.Bar() + 0x39").Lines);
        Assert.False(f.IsFrameLikeUnparsed);
        Assert.Equal("MyApp.Foo", f.TypeFullName);
        Assert.Equal("Bar", f.MethodName);
        Assert.Equal("", f.Parameters);
    }

    // ---- item 5 (#303): frame-like-but-unparsed lines surface in order ----

    [Fact]
    public void FrameLikeGarbage_SurfacesAsUnparsed_AtItsOriginalPosition()
    {
        var result = StackTraceParser.Parse("""
            System.InvalidOperationException: boom
               at ???bogus
               at Demo.Program.Main()
            """);
        Assert.Equal(3, result.Lines.Count);
        Assert.True(result.Lines[0].IsExceptionHeader);
        Assert.True(result.Lines[1].IsFrameLikeUnparsed);
        Assert.Equal("at ???bogus", result.Lines[1].Raw);
        Assert.False(result.Lines[2].IsFrameLikeUnparsed);
        Assert.Equal("Main", result.Lines[2].MethodName);
        Assert.Equal("at ???bogus", Assert.Single(result.FrameLikeUnparsed));
    }

    // ---- item 6: Framework same-line inner exception chain ----

    [Fact]
    public void SameLineInnerException_SplitsIntoTwoHeaders_InOrder()
    {
        var lines = StackTraceParser.Parse(
            "System.InvalidOperationException: outer ---> System.ArgumentNullException: nope").Lines;
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].IsExceptionHeader);
        Assert.Equal("System.InvalidOperationException", lines[0].TypeFullName);
        Assert.True(lines[1].IsExceptionHeader);
        Assert.Equal("System.ArgumentNullException", lines[1].TypeFullName);
    }

    // ---- item 7: generic exception headers ----

    [Fact]
    public void GenericExceptionHeader_NormalizesTypeName()
    {
        var f = Assert.Single(StackTraceParser.Parse(
            "Demo.ValidationException`1[Demo.Order]: order invalid").Lines);
        Assert.True(f.IsExceptionHeader);
        Assert.Equal("Demo.ValidationException", f.TypeFullName);
    }

    // ---- item 8: bare single-line fallback ----

    [Fact]
    public void BareSingleLine_StateMachineFrame_ParsesViaFallback()
    {
        var f = Assert.Single(StackTraceParser.Parse("Demo.Svc.<Run>d__3.MoveNext()").Lines);
        Assert.Equal("Demo.Svc.<Run>d__3", f.TypeFullName);
        Assert.Equal("MoveNext", f.MethodName);
    }

    [Fact]
    public void BareSingleLine_WithoutParens_ParsesViaFallback()
    {
        var f = Assert.Single(StackTraceParser.Parse("Demo.Svc.<Run>d__3.MoveNext").Lines);
        Assert.Equal("Demo.Svc.<Run>d__3", f.TypeFullName);
        Assert.Equal("MoveNext", f.MethodName);
    }

    [Fact]
    public void MultiLineNoiseOnly_ReturnsNoLines()
        => Assert.Empty(StackTraceParser.Parse("noise line one\nnoise line two").Lines);

    // ---- shared normalizer (item 1) ----

    [Fact]
    public void Normalizer_StripsInstantiationsAndArity_ConvertsPlusNesting()
    {
        Assert.Equal("Demo.ValidationException",
            TypeNameNormalizer.Normalize("Demo.ValidationException`1[Demo.Order]"));
        Assert.Equal("A.B.C",
            TypeNameNormalizer.Normalize("A.B`2[[X, asm],[Y, asm]]+C"));
        Assert.Equal("A.B`2+C",
            TypeNameNormalizer.StripInstantiations("A.B`2[[X, asm],[Y, asm]]+C"));
    }

    // ---- item 11: demystified generic types carry <...> blocks the source index lacks ----

    [Fact]
    public void Normalizer_StripAngleGenerics_RemovesBalancedAngleBlocks()
    {
        Assert.Equal("Demo.Repository",
            TypeNameNormalizer.StripAngleGenerics("Demo.Repository<TKey, TValue>"));
        Assert.Equal("Ns.Outer.Inner",
            TypeNameNormalizer.StripAngleGenerics("Ns.Outer<T>.Inner<U, V>"));
        Assert.Equal("Demo.Plain",
            TypeNameNormalizer.StripAngleGenerics("Demo.Plain"));
    }
}
