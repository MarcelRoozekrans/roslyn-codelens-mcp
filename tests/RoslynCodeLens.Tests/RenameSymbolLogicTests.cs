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

    [Fact]
    public async Task Apply_WritesRenamedFilesToDisk()
    {
        var dir = Directory.CreateTempSubdirectory("rename-apply-").FullName;
        try
        {
            var path = Path.Combine(dir, "Widget.cs");
            await File.WriteAllTextAsync(path, BasicSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket", preview: false);

            Assert.True(result.Success);
            Assert.True(result.Applied);
            var onDisk = await File.ReadAllTextAsync(path);
            Assert.Contains("public class Sprocket", onDisk, StringComparison.Ordinal);
            Assert.DoesNotContain("class Widget", onDisk, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_LeavesDiskUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("rename-preview-").FullName;
        try
        {
            var path = Path.Combine(dir, "Widget.cs");
            await File.WriteAllTextAsync(path, BasicSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, BasicSource));

            var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");   // preview: true

            Assert.False(result.Applied);
            Assert.Equal(BasicSource, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task PreexistingErrorMentioningSymbol_IsNotAConflict()
    {
        // CS0117's message embeds the type name ("'Widget' does not contain a
        // definition for 'NoSuchMember'"). After the rename the same error is
        // still there, just mentioning 'Sprocket' — the error COUNT for
        // (CS0117, file) is unchanged, so it must not be reported as a conflict.
        const string source = """
            namespace RenameDemo;

            public class Widget
            {
                public static int Existing = 1;
            }

            public class User
            {
                public object Bad = Widget.NoSuchMember;
                public int Good = Widget.Existing;
            }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Widget.cs", source));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");

        Assert.True(result.Success);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task SameIdSameFileNewLine_IsReported()
    {
        // Pre-existing CS0101 on the duplicate 'Dup' (line 3). Renaming
        // First -> Dup introduces a SECOND CS0101 in the same file with the
        // exact same message text — only the line differs. The count-based
        // diff must report exactly the new one, on the new line.
        const string source = """
            namespace RenameDemo;
            public class Dup { }
            public class Dup { }
            public class First { }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Types.cs", source));
        var result = await RunAsync(loaded, resolver, "First", "Dup");

        Assert.True(result.Success);
        Assert.NotEmpty(result.Conflicts);
        var conflict = Assert.Single(
            result.Conflicts, c => string.Equals(c.Id, "CS0101", StringComparison.Ordinal));
        Assert.Equal(4, conflict.Line);   // the renamed declaration, not the pre-existing dup (line 3)
    }

    // NOTE on the dependent-project-break scenario: a rename that genuinely breaks
    // a downstream project WITHOUT changing its text is not constructible with
    // AdhocWorkspace. Name-capture attempts (rename LibA's Foo -> Action while LibB
    // uses System.Action) are defused by Renamer's own conflict engine, which
    // complexifies the captured reference in LibB to 'System.Action' — so LibB's
    // text changes and it lands in GetProjectChanges anyway (verified empirically).
    // The remaining real-world breaks (source generators, name-based reflection
    // lookups) need generator/analyzer infrastructure AdhocWorkspace doesn't run.
    // The scan-set mechanic is therefore tested directly instead: dependents ARE
    // scanned (ComputeScanSet_IncludesDirectDependents) and scanning them applies
    // the same count-based diff without phantom conflicts or crashes
    // (DependentProjectWithPreexistingError_IsNotAConflict).
    [Fact]
    public async Task ComputeScanSet_IncludesDirectDependents()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("LibA", [("Widget.cs", "namespace Lib; public class Widget { }")]),
            ("LibB", [("Other.cs", "namespace Dep; public class Other { }")]));

        var target = RenameSymbolLogic.ResolveSingleTarget(resolver, "Widget");
        var renamed = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
            loaded.Solution, target,
            new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(), "Sprocket",
            CancellationToken.None);

        var scanSet = RenameSymbolLogic.ComputeScanSet(loaded.Solution, renamed);
        var libA = loaded.Solution.Projects.Single(p => string.Equals(p.Name, "LibA", StringComparison.Ordinal)).Id;
        var libB = loaded.Solution.Projects.Single(p => string.Equals(p.Name, "LibB", StringComparison.Ordinal)).Id;

        Assert.Contains(libA, scanSet);   // textually changed by the rename
        Assert.Contains(libB, scanSet);   // untouched, but a direct dependent of LibA
        Assert.Equal(scanSet.Count, scanSet.Distinct().Count());
    }

    [Fact]
    public async Task DependentProjectWithPreexistingError_IsNotAConflict()
    {
        // LibB is scanned as a direct dependent; its pre-existing CS0246 must not
        // surface as a conflict (count unchanged by the rename in LibA).
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("LibA", [("Widget.cs", "namespace Lib; public class Widget { }")]),
            ("LibB", [("Broken.cs", "namespace Dep; public class Broken { public Unknown Field; }")]));
        var result = await RunAsync(loaded, resolver, "Widget", "Sprocket");

        Assert.True(result.Success);
        Assert.Empty(result.Conflicts);
    }

    private static Diagnostic MakeError(string id, string path, int line, bool suppressed = false)
    {
        var linePosition = new Microsoft.CodeAnalysis.Text.LinePositionSpan(
            new Microsoft.CodeAnalysis.Text.LinePosition(line - 1, 0),
            new Microsoft.CodeAnalysis.Text.LinePosition(line - 1, 1));
        var location = Location.Create(
            path, new Microsoft.CodeAnalysis.Text.TextSpan(0, 0), linePosition);
        return Diagnostic.Create(
            id, "Test", "synthetic error",
            DiagnosticSeverity.Error, DiagnosticSeverity.Error,
            isEnabledByDefault: true, warningLevel: 0, isSuppressed: suppressed,
            location: location);
    }

    [Fact]
    public void SuppressedDiagnostics_AreIgnored()
    {
        // An IsSuppressed Error-severity diagnostic is not constructible through
        // AdhocWorkspace compilation options: #pragma/SuppressMessage suppression is
        // applied BEFORE warning-as-error elevation, so a suppressed diagnostic
        // surfaces (under ReportSuppressedDiagnostics) at its original Warning
        // severity, never Error. Suppressors that keep Error severity require
        // CompilationWithAnalyzers. The filter is therefore exercised at the diff
        // level with a synthetic suppressed diagnostic (Diagnostic.Create supports
        // isSuppressed directly).
        var suppressed = MakeError("CS9999", "File.cs", 3, suppressed: true);
        Assert.Empty(RenameSymbolLogic.DiffNewErrors(before: [], after: [suppressed]));

        var unsuppressed = MakeError("CS9999", "File.cs", 3);
        var conflict = Assert.Single(RenameSymbolLogic.DiffNewErrors(before: [], after: [unsuppressed]));
        Assert.Equal("CS9999", conflict.Id);
        Assert.Equal(3, conflict.Line);
    }

    [Fact]
    public void DiffNewErrors_PrefersLinesAbsentFromBefore()
    {
        var before = new[] { MakeError("CS0101", "Types.cs", 3) };
        var after = new[] { MakeError("CS0101", "Types.cs", 3), MakeError("CS0101", "Types.cs", 7) };

        var conflict = Assert.Single(RenameSymbolLogic.DiffNewErrors(before, after));
        Assert.Equal(7, conflict.Line);
    }

    [Fact]
    public async Task CollidingRename_ForceTrue_WritesAnyway()
    {
        var dir = Directory.CreateTempSubdirectory("rename-force-").FullName;
        try
        {
            var path = Path.Combine(dir, "Types.cs");
            await File.WriteAllTextAsync(path, CollisionSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, CollisionSource));

            var result = await RunAsync(loaded, resolver, "First", "Second", preview: false, force: true);

            Assert.True(result.Applied);
            Assert.NotEmpty(result.Conflicts);
            Assert.Contains("class Second", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
