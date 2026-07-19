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
