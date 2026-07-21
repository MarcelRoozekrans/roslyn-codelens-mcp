using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class GetExtensionMethodsLogicTests
{
    private const string Source = """
        using System.Collections.Generic;

        namespace Demo;

        public class Widget { }

        public static class IntExtensions
        {
            /// <summary>Doubles the value.</summary>
            public static int Doubled(this int value) => value * 2;
        }

        public static class SeqExtensions
        {
            public static T First2<T>(this IEnumerable<T> source) => default!;
        }

        public static class StringSeqExtensions
        {
            public static string Join2(this IEnumerable<string> source) => "";
        }

        public static class ListExtensions
        {
            public static int Chunkify(this List<int> source) => 0;
        }

        public static class BlockExtensions
        {
            extension(int value)
            {
                public int Tripled => value * 3;
                public int Thrice() => value * 3;
                public static int Zero => 0;
            }
        }
        """;

    private static IReadOnlyList<ExtensionMemberInfo> Run(string type, string? nameFilter = null)
        => RunOn(Source, type, nameFilter);

    private static IReadOnlyList<ExtensionMemberInfo> RunOn(
        string source, string type, string? nameFilter = null)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(
            RenameTestWorkspaceOptions.FrameworkReferences | RenameTestWorkspaceOptions.PreviewLanguage,
            ("Extensions.cs", source));
        return GetExtensionMethodsLogic.Execute(
            loaded, resolver, new MetadataSymbolResolver(loaded, resolver), type, nameFilter);
    }

    private static IReadOnlyList<string> Names(IReadOnlyList<ExtensionMemberInfo> items)
        => items.Select(i => i.Name).ToList();

    [Fact]
    public void SimpleExtension_AppliesToItsReceiver()
    {
        Assert.Contains("Doubled", Names(Run("int")));
    }

    [Fact]
    public void SimpleExtension_DoesNotApplyToOtherTypes()
    {
        Assert.DoesNotContain("Doubled", Names(Run("string")));
    }

    [Fact]
    public void GenericExtension_AppliesViaInference()
    {
        Assert.Contains("First2", Names(Run("List<int>")));
    }

    [Fact]
    public void GenericExtension_AppliesToString()
    {
        // string is IEnumerable<char>, so `this IEnumerable<T>` binds with T = char.
        Assert.Contains("First2", Names(Run("string")));
    }

    [Fact]
    public void MismatchedGenericExtension_DoesNotApply()
    {
        // `this IEnumerable<string>` must NOT apply to string (which is IEnumerable<char>).
        // The control assertion below keeps this from passing because nothing resolved at all.
        Assert.DoesNotContain("Join2", Names(Run("string")));
        Assert.Contains("Join2", Names(Run("List<string>")));
    }

    [Fact]
    public void BclLinq_IsReported()
    {
        var where = Run("List<int>").Where(i => i.Name == "Where").ToList();

        Assert.NotEmpty(where);
        Assert.All(where, w => Assert.Equal("metadata", w.Origin));
        Assert.All(where, w => Assert.Equal("System.Linq", w.Namespace));
    }

    [Fact]
    public void CSharp14BlockMethod_IsReported()
    {
        var thrice = Assert.Single(Run("int"), i => i.Name == "Thrice");

        Assert.Equal("method", thrice.Kind);
        Assert.Equal("Demo.BlockExtensions", thrice.DeclaringType);
    }

    [Fact]
    public void CSharp14BlockProperty_IsReported()
    {
        // Extension properties surface only as `get_Tripled` with IsExtensionMethod == false,
        // so an IsExtensionMethod-only scan misses them entirely.
        var tripled = Assert.Single(Run("int"), i => i.Name == "Tripled");

        Assert.Equal("property", tripled.Kind);
        Assert.Equal("Demo.BlockExtensions", tripled.DeclaringType);
        Assert.Equal("source", tripled.Origin);
        Assert.False(tripled.IsStatic);
    }

    /// <summary>
    /// A C# 14 block may declare static members, and they are invoked on the TYPE
    /// (<c>int.Zero</c>) rather than on an instance. Both are genuinely applicable, so both are
    /// reported — but a caller told only "Zero applies to int" would write <c>value.Zero</c> and
    /// be wrong, so the distinction has to survive into the result.
    /// </summary>
    [Fact]
    public void StaticExtensionProperty_IsReportedAndMarkedStatic()
    {
        var zero = Assert.Single(Run("int"), i => i.Name == "Zero");

        Assert.Equal("property", zero.Kind);
        Assert.True(zero.IsStatic);
    }

    [Fact]
    public void NameFilter_Narrows()
    {
        var results = Run("List<int>", "chunk");

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Contains("chunk", r.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Chunkify", Names(results));
        Assert.Contains("Chunk", Names(results));
    }

    [Fact]
    public void SourceExtensionsSortBeforeMetadata()
    {
        var results = Run("List<int>").ToList();

        var lastSource = results.FindLastIndex(r => r.Origin == "source");
        var firstMetadata = results.FindIndex(r => r.Origin == "metadata");

        Assert.True(lastSource >= 0, "expected at least one source extension");
        Assert.True(firstMetadata >= 0, "expected at least one metadata extension");
        Assert.True(lastSource < firstMetadata, "source extensions must sort before metadata ones");
    }

    [Fact]
    public void UnreferencedProjectExtension_IsNotReported()
    {
        const string receiverProject = "namespace Demo; public class Widget { }";
        const string extensionProject = """
            namespace Other;

            using Demo;

            public static class WidgetExtensions
            {
                public static int Boost(this Widget widget) => 1;
            }
            """;

        // "Receiver" is added first, so it does NOT reference "Extensions"; the extension is
        // therefore not callable from Widget's own project and must not be reported.
        var (loaded, resolver) = RenameTestWorkspace.Create(
            RenameTestWorkspaceOptions.None,
            ("Receiver", [("Widget.cs", receiverProject)]),
            ("Extensions", [("WidgetExtensions.cs", extensionProject)]));

        var results = GetExtensionMethodsLogic.Execute(
            loaded, resolver, new MetadataSymbolResolver(loaded, resolver), "Widget", null);

        Assert.DoesNotContain("Boost", Names(results));
    }

    [Fact]
    public void ReferencedProjectExtension_IsReported()
    {
        // Control for UnreferencedProjectExtension_IsNotReported: identical shape with the
        // extension in the receiver's own project, proving the absence there is about scope
        // and not about Widget failing to resolve.
        const string source = """
            namespace Demo;

            public class Widget { }

            public static class WidgetExtensions
            {
                public static int Boost(this Widget widget) => 1;
            }
            """;

        Assert.Contains("Boost", Names(RunOn(source, "Widget")));
    }

    [Fact]
    public void UnknownType_Throws_SymbolNotFound()
    {
        var ex = Assert.Throws<McpToolException>(() => Run("NoSuchTypeAnywhere"));
        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }

    [Fact]
    public void NamespaceArgument_Throws_InvalidArgument()
    {
        var ex = Assert.Throws<McpToolException>(() => Run("Demo"));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void Origin_And_Location_AreCorrect()
    {
        var doubled = Assert.Single(Run("int"), i => i.Name == "Doubled");
        Assert.Equal("source", doubled.Origin);
        Assert.Equal("Extensions.cs", doubled.File);
        Assert.True(doubled.Line > 0);
        Assert.Equal("Doubles the value.", doubled.XmlDocSummary);

        var where = Run("List<int>").First(i => i.Name == "Where");
        Assert.Equal("metadata", where.Origin);
        Assert.Null(where.File);
        Assert.Null(where.Line);
    }
}
