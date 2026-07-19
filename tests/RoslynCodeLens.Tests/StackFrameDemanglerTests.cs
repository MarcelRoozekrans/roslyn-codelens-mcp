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

    // ---- item 2: modern dot-nested state-machine forms (empirical fixtures) ----

    [Fact]
    public void AsyncLambdaStateMachine_DotNested_MapsToLambdaInEnclosingMethod()
    {
        // at Program.<>c.<<Case1_AsyncLambda>b__8_0>d.MoveNext()
        var d = D("Program.<>c.<<Case1_AsyncLambda>b__8_0>d", "MoveNext");
        Assert.Equal("Program", d.TypeName);
        Assert.Equal(DemangledKind.Lambda, d.Kind);
        Assert.Equal("Case1_AsyncLambda", d.EnclosingMethod);
        Assert.Equal("Case1_AsyncLambda", d.MethodName);
        Assert.Equal("Program.<>c.<<Case1_AsyncLambda>b__8_0>d", d.RuntimeTypeName);
    }

    [Fact]
    public void LocalFunction_InDotNestedDisplayClass_SingleIndexSuffix_Maps()
    {
        // at Program.<>c__DisplayClass10_0.<Case2b_CapturingLocalFunctionWithLambda>g__Boom|1()
        var d = D("Program.<>c__DisplayClass10_0", "<Case2b_CapturingLocalFunctionWithLambda>g__Boom|1");
        Assert.Equal("Program", d.TypeName);
        Assert.Equal(DemangledKind.LocalFunction, d.Kind);
        Assert.Equal("Case2b_CapturingLocalFunctionWithLambda", d.EnclosingMethod);
        Assert.Equal("Boom", d.LocalFunctionName);
    }

    [Fact]
    public void LocalFunction_HostedOnType_DisplayStructByRefParam_Maps()
    {
        // at Program.<Case2_CapturingLocalFunction>g__Boom|9_0(<>c__DisplayClass9_0&)
        var d = D("Program", "<Case2_CapturingLocalFunction>g__Boom|9_0");
        Assert.Equal("Program", d.TypeName);
        Assert.Equal(DemangledKind.LocalFunction, d.Kind);
        Assert.Equal("Case2_CapturingLocalFunction", d.EnclosingMethod);
        Assert.Equal("Boom", d.LocalFunctionName);
    }

    [Fact]
    public void AsyncLocalFunctionStateMachine_MapsToLocalFunction()
    {
        var d = D("Demo.Svc.<<Run>g__Boom|0_0>d", "MoveNext");
        Assert.Equal("Demo.Svc", d.TypeName);
        Assert.Equal(DemangledKind.LocalFunction, d.Kind);
        Assert.Equal("Run", d.EnclosingMethod);
        Assert.Equal("Boom", d.LocalFunctionName);
    }

    [Fact]
    public void ClassicStateMachine_DotNested_Maps()
    {
        var d = D("Demo.Svc.<Run>d__3", "MoveNext");
        Assert.Equal("Demo.Svc", d.TypeName);
        Assert.Equal(DemangledKind.StateMachine, d.Kind);
        Assert.Equal("Run", d.MethodName);
    }

    [Fact]
    public void LambdaContainer_DotNested_MapsToEnclosingMethod()
    {
        var d = D("Demo.OrderService.<>c", "<Process>b__5_0");
        Assert.Equal("Demo.OrderService", d.TypeName);
        Assert.Equal(DemangledKind.Lambda, d.Kind);
        Assert.Equal("Process", d.EnclosingMethod);
    }

    // ---- item 1: instantiation blocks + RuntimeTypeName plumbing ----

    [Fact]
    public void GenericInstantiation_IsStripped_RuntimeTypeNameKeepsArity()
    {
        var d = D("Demo.Repository`1[[System.Int32, System.Private.CoreLib]]", "GetById");
        Assert.Equal("Demo.Repository", d.TypeName);
        Assert.Equal("Demo.Repository`1", d.RuntimeTypeName);
        Assert.Equal(DemangledKind.Plain, d.Kind);
    }

    [Fact]
    public void RuntimeTypeName_PreservesPlusNestingAndArity()
    {
        var d = D("Demo.Repository`1+Enumerator", "MoveNext");
        Assert.Equal("Demo.Repository.Enumerator", d.TypeName);
        Assert.Equal("Demo.Repository`1+Enumerator", d.RuntimeTypeName);
    }
}
