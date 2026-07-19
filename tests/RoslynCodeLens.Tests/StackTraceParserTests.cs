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
}
