using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class ChangeSignatureLogicTests
{
    private static Task<ChangeSignatureResult> RunAsync(
        LoadedSolution loaded, SymbolResolver resolver, string method,
        IReadOnlyList<SignatureOperation> operations,
        bool preview = true, bool force = false,
        CommitWrittenDocuments? commit = null)
        => ChangeSignatureLogic.ExecuteAsync(
            loaded, resolver, method, operations, preview, force, commit, CancellationToken.None);

    /// <summary>Applies preview edits for one file back onto its source, as the rename tests do.</summary>
    private static string ApplyEdits(string source, IEnumerable<TextEdit> edits, string filePath)
    {
        var text = SourceText.From(source);
        var changes = edits
            .Where(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            .Select(e => new TextChange(
                TextSpan.FromBounds(
                    text.Lines[e.StartLine - 1].Start + e.StartColumn - 1,
                    text.Lines[e.EndLine - 1].Start + e.EndColumn - 1),
                e.NewText));
        return text.WithChanges(changes).ToString();
    }

    private static (LoadedSolution Loaded, SymbolResolver Resolver) Degrade(
        (LoadedSolution Loaded, SymbolResolver Resolver) workspace)
    {
        var degraded = new LoadedSolution
        {
            Solution = workspace.Loaded.Solution,
            Compilations = workspace.Loaded.Compilations,
            LoadDiagnostics = ["SigProj: metadata reference 'Missing.dll' could not be resolved"],
        };
        return (degraded, new SymbolResolver(degraded));
    }

    // ---------------------------------------------------------------- operations

    private const string TwoParamSource = """
        namespace SigDemo;
        public class Svc
        {
            public int Add(int a, string b) => a;
            public int Use() => Add(1, "x");
        }
        """;

    [Fact]
    public async Task Remove_DropsParameterAndUpdatesCallSites()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("remove", Parameter: "b")]);

        Assert.True(result.Success, result.Message);
        Assert.False(result.Applied);
        var after = ApplyEdits(TwoParamSource, result.Edits, "Svc.cs");
        Assert.Contains("Add(int a)", after, StringComparison.Ordinal);
        Assert.Contains("Add(1)", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reorder_RewritesCallSitesInNewOrder()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("reorder", Order: ["b", "a"])]);

        Assert.True(result.Success, result.Message);
        var after = ApplyEdits(TwoParamSource, result.Edits, "Svc.cs");
        Assert.Contains("Add(string b, int a)", after, StringComparison.Ordinal);
        Assert.Contains("Add(\"x\", 1)", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reorder_ReportsOldAndNewSignature()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("reorder", Order: ["b", "a"])]);

        Assert.Equal("Add(int a, string b)", result.OldSignature);
        Assert.Equal("Add(string b, int a)", result.NewSignature);
        Assert.Equal("SigDemo.Svc.Add(int, string)", result.Method);
    }

    private const string OneParamSource = """
        using System.Threading;

        namespace SigDemo;
        public class Svc
        {
            public int Add(int a) => a;
            public int Use() => Add(1);
        }
        """;

    /// <summary>
    /// Also the proof for the bridge's positionForTypeBinding choice: the added parameter's type
    /// name is bound/simplified with the file's usings in scope, so `System.Threading.CancellationToken`
    /// renders as `CancellationToken` rather than degrading.
    /// </summary>
    [Fact]
    public async Task Add_InsertsCallSiteValueEverywhere()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", OneParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
        [
            new SignatureOperation("add",
                Name: "token", Type: "System.Threading.CancellationToken",
                CallSiteValue: "CancellationToken.None"),
        ]);

        Assert.True(result.Success, result.Message);
        var after = ApplyEdits(OneParamSource, result.Edits, "Svc.cs");
        Assert.Contains("CancellationToken token", after, StringComparison.Ordinal);
        Assert.Contains("Add(1, CancellationToken.None)", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_WithDefault_LeavesExistingCallSitesAlone()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", OneParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
        [
            new SignatureOperation("add",
                Name: "token", Type: "System.Threading.CancellationToken",
                CallSiteValue: "CancellationToken.None", DefaultValue: "default"),
        ]);

        Assert.True(result.Success, result.Message);
        var after = ApplyEdits(OneParamSource, result.Edits, "Svc.cs");
        Assert.Contains("CancellationToken token = default", after, StringComparison.Ordinal);
        // isRequired:false + CallSiteKind.Omitted ⇒ the existing call keeps its single argument.
        Assert.Contains("Add(1)", after, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedArguments_AreRewritten()
    {
        const string source = """
            namespace SigDemo;
            public class Svc
            {
                public int Add(int a, string b) => a;
                public int Use() => Add(a: 1, b: "x");
            }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", source));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("reorder", Order: ["b", "a"])]);

        Assert.True(result.Success, result.Message);
        var after = ApplyEdits(source, result.Edits, "Svc.cs");
        Assert.Contains("Add(string b, int a)", after, StringComparison.Ordinal);
        Assert.Contains("Add(b: \"x\", a: 1)", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Combined_RemoveThenAdd_AppliesInOrder()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
        [
            new SignatureOperation("remove", Parameter: "b"),
            new SignatureOperation("add", Name: "count", Type: "System.Int32", CallSiteValue: "0"),
        ]);

        Assert.True(result.Success, result.Message);
        var after = ApplyEdits(TwoParamSource, result.Edits, "Svc.cs");
        Assert.Contains("Add(int a, int count)", after, StringComparison.Ordinal);
        Assert.Contains("Add(1, 0)", after, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- cascade

    [Fact]
    public async Task InterfaceImplementation_IsCascaded()
    {
        const string source = """
            namespace SigDemo;
            public interface IGreet
            {
                string Greet(string name, int times);
            }
            public class Greeter : IGreet
            {
                public string Greet(string name, int times) => name;
            }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Greet.cs", source));

        var result = await RunAsync(loaded, resolver, "IGreet.Greet",
            [new SignatureOperation("remove", Parameter: "times")]);

        Assert.True(result.Success, result.Message);
        Assert.Contains(result.CascadedTo, s => s.Contains("Greeter.Greet", StringComparison.Ordinal));
        var after = ApplyEdits(source, result.Edits, "Greet.cs");
        Assert.DoesNotContain("int times", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Override_IsCascaded()
    {
        const string source = """
            namespace SigDemo;
            public class Base
            {
                public virtual string Greet(string name, int times) => name;
            }
            public class Derived : Base
            {
                public override string Greet(string name, int times) => name;
            }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Greet.cs", source));

        var result = await RunAsync(loaded, resolver, "Base.Greet",
            [new SignatureOperation("remove", Parameter: "times")]);

        Assert.True(result.Success, result.Message);
        Assert.Contains(result.CascadedTo, s => s.Contains("Derived.Greet", StringComparison.Ordinal));
        var after = ApplyEdits(source, result.Edits, "Greet.cs");
        Assert.DoesNotContain("int times", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtensionMethod_ThisParameterPreserved()
    {
        const string source = """
            namespace SigDemo;
            public static class Ext
            {
                public static int Twice(this int value, int factor, string label) => value;
                public static int Use() => 3.Twice(2, "x");
            }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Ext.cs", source));

        var result = await RunAsync(loaded, resolver, "Ext.Twice",
            [new SignatureOperation("reorder", Order: ["value", "label", "factor"])]);

        Assert.True(result.Success, result.Message);
        var after = ApplyEdits(source, result.Edits, "Ext.cs");
        Assert.Contains("Twice(this int value, string label, int factor)", after, StringComparison.Ordinal);
        Assert.Contains("3.Twice(\"x\", 2)", after, StringComparison.Ordinal);
    }

    private const string ExtensionSource = """
        namespace SigDemo;
        public static class Ext
        {
            public static int Twice(this int value, int factor, string label) => value;
            public static int Use() => 3.Twice(2, "x");
        }
        """;

    /// <summary>
    /// The receiver is index 0 by position, not by identity: Roslyn's ParameterConfiguration
    /// re-derives `this` from whatever lands first. A reorder that promotes another parameter
    /// would silently rebind the receiver, so it is refused.
    /// </summary>
    [Fact]
    public async Task ExtensionMethod_ReorderDisplacingReceiver_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Ext.cs", ExtensionSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Ext.Twice",
                [new SignatureOperation("reorder", Order: ["factor", "value", "label"])]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("extension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtensionMethod_RemovingReceiver_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Ext.cs", ExtensionSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Ext.Twice",
                [new SignatureOperation("remove", Parameter: "value")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("extension", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_DuplicateParameterName_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("add", Name: "a", Type: "System.Int32", CallSiteValue: "0")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("'a'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_NameDifferingOnlyByCase_IsAllowed()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        // C# identifiers are case-sensitive, so `A` alongside `a` is legal.
        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("add", Name: "A", Type: "System.Int32", CallSiteValue: "0")]);

        Assert.True(result.Success, result.Message);
    }

    private const string ParamsSource = """
        namespace SigDemo;
        public class Svc
        {
            public int Sum(int a, params int[] rest) => a;
            public int Use() => Sum(1, 2, 3);
        }
        """;

    /// <summary>
    /// Roslyn recognises `params` only on the LAST parameter, so an add that lands after it
    /// would emit CS0231. Refused rather than silently reordered.
    /// </summary>
    [Fact]
    public async Task ParamsMethod_Add_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", ParamsSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Sum",
                [new SignatureOperation("add", Name: "seed", Type: "System.Int32", CallSiteValue: "0")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("params", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParamsMethod_ReorderMovingParamsOffTheEnd_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", ParamsSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Sum",
                [new SignatureOperation("reorder", Order: ["rest", "a"])]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("params", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParamsMethod_RemovingTheParamsArray_IsAllowed()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", ParamsSource));

        var result = await RunAsync(loaded, resolver, "Svc.Sum",
            [new SignatureOperation("remove", Parameter: "rest")]);

        Assert.True(result.Success, result.Message);
    }

    [Theory]
    [InlineData("1st")]
    [InlineData("x, int y")]
    [InlineData("a b")]
    // NOTE: a reserved keyword ("class") is NOT rejected — SyntaxFacts.IsValidIdentifier checks
    // lexical form only, and this deliberately uses the same guard as rename_symbol. Such a name
    // produces parse errors that surface as Conflicts, so it cannot land silently.
    public async Task Add_InvalidIdentifier_ThrowsInvalidArgument(string name)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("add", Name: name, Type: "System.Int32", CallSiteValue: "0")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("identifier", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- conflicts

    private const string BodyUsesParamSource = """
        namespace SigDemo;
        public class Svc
        {
            public int Add(int a, string b) => a + b.Length;
            public int Use() => Add(1, "x");
        }
        """;

    [Fact]
    public async Task RemovedParameterStillUsed_ProducesConflict()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", BodyUsesParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("remove", Parameter: "b")]);

        Assert.True(result.Success, result.Message);
        // The body still says `b.Length` after `b` is gone: CS0103 (name does not exist).
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("CS0103", conflict.Id);
        Assert.Contains("b", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovedParameterStillUsed_Apply_RefusesWithoutForce()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", BodyUsesParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("remove", Parameter: "b")], preview: false);

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.NotEmpty(result.Conflicts);
        Assert.Contains("force", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- resolution errors

    [Fact]
    public async Task Overloads_AreAmbiguous()
    {
        const string source = """
            namespace SigDemo;
            public class Svc
            {
                public int Add(int a) => a;
                public int Add(int a, string b) => a;
            }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", source));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("remove", Parameter: "a")]));

        Assert.Equal(ToolErrorCode.AmbiguousMatch, ex.Code);
        Assert.Contains("Add(int, string)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownMethod_ThrowsSymbolNotFound()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.NoSuchMethod",
                [new SignatureOperation("remove", Parameter: "a")]));

        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }

    [Fact]
    public async Task NonMethodTarget_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc",
                [new SignatureOperation("remove", Parameter: "a")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task UnknownParameter_ThrowsSymbolNotFound()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("remove", Parameter: "nope")]));

        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- argument validation

    [Fact]
    public async Task EmptyOperations_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add", []));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task NullOperations_ThrowsInvalidArgumentLikeAnEmptyArray()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add", null!));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("No operations supplied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownKind_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("rename", Parameter: "a")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task ReorderNotAPermutation_ThrowsInvalidArgumentNamingTheDifference()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("reorder", Order: ["a"])]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReorderWithUnknownName_ThrowsInvalidArgumentNamingTheDifference()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("reorder", Order: ["a", "zzz"])]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("zzz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddWithoutCallSiteValue_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("add", Name: "token", Type: "System.Int32")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("callSiteValue", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddWithoutNameOrType_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var missingName = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("add", Type: "System.Int32", CallSiteValue: "0")]));
        Assert.Equal(ToolErrorCode.InvalidArgument, missingName.Code);

        var missingType = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("add", Name: "token", CallSiteValue: "0")]));
        Assert.Equal(ToolErrorCode.InvalidArgument, missingType.Code);
    }

    [Fact]
    public async Task RemoveWithoutParameterName_ThrowsInvalidArgument()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "Svc.Add", [new SignatureOperation("remove")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task RemovingEveryParameter_IsAllowed()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
        [
            new SignatureOperation("remove", Parameter: "a"),
            new SignatureOperation("remove", Parameter: "b"),
        ]);

        Assert.True(result.Success, result.Message);
        var after = ApplyEdits(TwoParamSource, result.Edits, "Svc.cs");
        Assert.Contains("Add()", after, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- degraded load

    [Fact]
    public async Task Degraded_Preview_MessageCarriesWarning()
    {
        var (loaded, resolver) = Degrade(RenameTestWorkspace.Create(("Svc.cs", TwoParamSource)));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("remove", Parameter: "b")]);

        Assert.True(result.Success, result.Message);
        Assert.Contains("degraded", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 load diagnostic", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Degraded_Apply_RefusedWithoutForce()
    {
        var commitCalls = 0;
        var (loaded, resolver) = Degrade(RenameTestWorkspace.Create(("Svc.cs", TwoParamSource)));

        var result = await RunAsync(loaded, resolver, "Svc.Add",
            [new SignatureOperation("remove", Parameter: "b")], preview: false,
            commit: (_, _) => { commitCalls++; return Task.CompletedTask; });

        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.Contains("degraded", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuild_solution", result.Message, StringComparison.Ordinal);
        Assert.Contains("force=true", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, commitCalls);
    }

    // ---------------------------------------------------------------- write path

    [Fact]
    public async Task Preview_LeavesDiskUntouched()
    {
        var dir = Directory.CreateTempSubdirectory("sig-preview-").FullName;
        try
        {
            var path = Path.Combine(dir, "Svc.cs");
            await File.WriteAllTextAsync(path, TwoParamSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, TwoParamSource));

            var commitCalls = 0;
            var result = await RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("remove", Parameter: "b")],
                commit: (_, _) => { commitCalls++; return Task.CompletedTask; });

            Assert.False(result.Applied);
            Assert.Equal(0, commitCalls);
            Assert.Equal(TwoParamSource, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_WritesAndCommits()
    {
        var dir = Directory.CreateTempSubdirectory("sig-apply-").FullName;
        try
        {
            var path = Path.Combine(dir, "Svc.cs");
            await File.WriteAllTextAsync(path, TwoParamSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, TwoParamSource));

            var commits = new List<IReadOnlyList<(DocumentId Id, SourceText Text)>>();
            var result = await RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("remove", Parameter: "b")], preview: false,
                commit: (docs, _) => { commits.Add(docs); return Task.CompletedTask; });

            Assert.True(result.Success, result.Message);
            Assert.True(result.Applied);
            Assert.Equal(1, result.FilesChanged);

            var onDisk = await File.ReadAllTextAsync(path);
            Assert.Contains("Add(int a)", onDisk, StringComparison.Ordinal);
            Assert.Contains("Add(1)", onDisk, StringComparison.Ordinal);

            var docs = Assert.Single(commits);
            var pair = Assert.Single(docs);
            Assert.Equal(loaded.Solution.GetDocumentIdsWithFilePath(path).Single(), pair.Id);
            Assert.Equal(onDisk, pair.Text.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_DiskChangedSinceSnapshot_RefusedAndFileNotOverwritten()
    {
        var dir = Directory.CreateTempSubdirectory("sig-stale-").FullName;
        try
        {
            var path = Path.Combine(dir, "Svc.cs");
            await File.WriteAllTextAsync(path, TwoParamSource);
            var (loaded, resolver) = RenameTestWorkspace.Create((path, TwoParamSource));

            var drifted = TwoParamSource + "\n// concurrent edit\n";
            await File.WriteAllTextAsync(path, drifted);

            var result = await RunAsync(loaded, resolver, "Svc.Add",
                [new SignatureOperation("remove", Parameter: "b")], preview: false);

            Assert.False(result.Success);
            Assert.False(result.Applied);
            Assert.Contains("rebuild_solution", result.Message, StringComparison.Ordinal);
            Assert.Equal(drifted, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------- Roslyn's own refusal

    /// <summary>
    /// Roslyn's <c>ChangeSignatureFailureKind</c> is narrow (None / DefinedInMetadata /
    /// IncorrectKind) and both failure kinds are produced by the ANALYSIS step, which this design
    /// bypasses by constructing a succeeded context from an already-resolved symbol. The
    /// metadata-defined case is therefore refused one layer earlier, by the logic: a method with
    /// no source declaration has no document to drive the change from. This test pins that
    /// refusal so the metadata path can never reach the bridge silently.
    /// </summary>
    [Fact]
    public async Task MetadataMethod_IsRefusedBeforeTheBridge()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Svc.cs", TwoParamSource));

        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => RunAsync(loaded, resolver, "System.Object.GetHashCode",
                [new SignatureOperation("add", Name: "seed", Type: "int", CallSiteValue: "0")]));

        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
