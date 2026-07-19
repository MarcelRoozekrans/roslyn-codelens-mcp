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

    private static string ApplyEditsToSource(string source, IEnumerable<TextEdit> edits, string filePath)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(source);
        var changes = edits
            .Where(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .Select(e => new Microsoft.CodeAnalysis.Text.TextChange(
                Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(
                    text.Lines[e.StartLine - 1].Start + e.StartColumn - 1,
                    text.Lines[e.EndLine - 1].Start + e.EndColumn - 1),
                e.NewText));
        return text.WithChanges(changes).ToString();
    }

    [Fact]
    public async Task RenameType_CascadesToUsagesCtorAndNameof()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");

        Assert.True(result.Success);
        Assert.False(result.Applied);
        Assert.Empty(result.Conflicts);

        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("public class Sprocket", after, StringComparison.Ordinal);
        Assert.Contains("public Sprocket()", after, StringComparison.Ordinal);
        Assert.Contains("new Sprocket().Compute(1)", after, StringComparison.Ordinal);
        Assert.Contains("nameof(Sprocket)", after, StringComparison.Ordinal);
        Assert.DoesNotContain("class Widget", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameInComments_OnByDefault_RewritesComment()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("// Sprocket appears in this comment.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameInComments_Off_LeavesComment()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", renameInComments: false);
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("// Widget appears in this comment.", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameInStrings_OffByDefault_LeavesString()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("\"Widget in a string\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameInStrings_On_RewritesString()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", renameInStrings: true);
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("\"Sprocket in a string\"", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenameOverloads_On_RenamesAllOverloadsAndCallSites()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", BasicSource));
        var result = await RunAsync(loaded, resolver, "Widget.Compute", "Calculate");
        var after = ApplyEditsToSource(BasicSource, result.Edits, "Widget.cs");
        Assert.Contains("public int Calculate(int value)", after, StringComparison.Ordinal);
        Assert.Contains("public int Calculate(int a, int b)", after, StringComparison.Ordinal);
        Assert.Contains(".Calculate(1)", after, StringComparison.Ordinal);
        Assert.Empty(result.Conflicts);
    }

    private const string CollisionSource = """
        namespace RenameDemo;
        public class First { }
        public class Second { }
        """;

    [Fact]
    public async Task CollidingRename_Preview_ReportsConflicts()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Types.cs", CollisionSource));
        var result = await RunAsync(loaded, resolver, "First", "Second");

        Assert.True(result.Success);
        Assert.False(result.Applied);
        Assert.NotEmpty(result.Conflicts);   // CS0101: duplicate type in namespace
        Assert.Contains(result.Conflicts, c => string.Equals(c.Id, "CS0101", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollidingRename_Apply_RefusesWithoutForce()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Types.cs", CollisionSource));
        var result = await RunAsync(loaded, resolver, "First", "Second", preview: false);

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.NotEmpty(result.Conflicts);
        Assert.Contains("force", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
