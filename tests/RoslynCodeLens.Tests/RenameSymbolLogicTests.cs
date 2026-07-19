using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class RenameSymbolLogicTests
{
    private const string BasicSource = """
        namespace RenameDemo;

        public class Widget
        {
            public Widget() { }
            public int Compute(int value) => value + 1;
            public int Compute(int a, int b) => a + b;
            // Widget appears in this comment.
            public string Marker = "Widget in a string";
            public string Describe() => nameof(Widget);
        }

        public class Gadget
        {
            public int Run() => new Widget().Compute(1);
        }
        """;

    private static Task<RenameSymbolResult> RunAsync(
        LoadedSolution loaded, SymbolResolver resolver, string symbol, string newName,
        bool renameOverloads = true, bool renameInStrings = false, bool renameInComments = true,
        bool preview = true, bool force = false)
        => RenameSymbolLogic.ExecuteAsync(
            loaded, resolver, symbol, newName,
            renameOverloads, renameInStrings, renameInComments, preview, force,
            CancellationToken.None);

    [Fact]
    public async Task InvalidIdentifier_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Widget", "123 bad name"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task UnknownSymbol_ThrowsSymbolNotFound()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "NoSuchType", "Whatever"));
        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }

    [Fact]
    public async Task AmbiguousSimpleName_ThrowsAmbiguousMatch()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("A.cs", "namespace NsA; public class Dup { }"),
            ("B.cs", "namespace NsB; public class Dup { }"));
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Dup", "Renamed"));
        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
    }

    [Fact]
    public void ConstructorTarget_ThrowsInvalidArgument()
    {
        var (loaded, _) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var compilation = loaded.Compilations.Values.First();
        var widget = compilation.GetTypeByMetadataName("RenameDemo.Widget")!;
        var ctor = widget.InstanceConstructors.First(c => !c.IsImplicitlyDeclared);

        var ex = Assert.Throws<McpToolException>(
            () => RenameSymbolLogic.ValidateRenameTarget(ctor, "Widget.Widget"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void MetadataTarget_ThrowsInvalidArgument()
    {
        var (loaded, _) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var compilation = loaded.Compilations.Values.First();
        var stringType = compilation.GetSpecialType(SpecialType.System_String);

        var ex = Assert.Throws<McpToolException>(
            () => RenameSymbolLogic.ValidateRenameTarget(stringType, "System.String"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task MethodOverloadGroup_IsNotAmbiguous()
    {
        // Widget.Compute has two overloads; that is ONE rename target, not an ambiguity.
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget.Compute", "Calculate");
        Assert.True(result.Success);
    }
}
