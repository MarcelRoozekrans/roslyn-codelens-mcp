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
                public static int MakeZero() => 0;
            }
        }

        public static class ArrayExtensions
        {
            public static T Firsty<T>(this T[] source) => default!;
        }

        public static class NullableExtensions
        {
            public static int OrZero(this int? value) => 0;
        }

        public static class TupleExtensions
        {
            public static int Combine(this (int, string) pair) => 0;
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

    // ------------------------------------------------------------------ IsStatic semantics
    //
    // IsStatic answers ONE question: is this member invoked on the TYPE (`int.Zero`) or on an
    // INSTANCE (`value.Doubled()`)? It is emphatically not "was the declaration written with the
    // static keyword" — every classic extension method is declared static, and reporting that
    // would tell an agent to write `int.Doubled()`, which does not compile.

    [Fact]
    public void ClassicExtension_IsNotMarkedStatic()
    {
        var doubled = Assert.Single(Run("int"), i => i.Name == "Doubled");

        Assert.False(doubled.IsStatic);
    }

    [Fact]
    public void BlockInstanceMethod_IsNotMarkedStatic()
    {
        var thrice = Assert.Single(Run("int"), i => i.Name == "Thrice");

        Assert.False(thrice.IsStatic);
    }

    [Fact]
    public void BclExtensions_AreNotMarkedStatic()
    {
        Assert.All(
            Run("List<int>").Where(i => i.Origin == "metadata"),
            i => Assert.False(i.IsStatic, $"{i.Name} is called on an instance"));
    }

    /// <summary>
    /// A block's static METHOD is invisible to pass A: unlike an instance block method, the
    /// compiler lifts it onto the container with <c>IsExtensionMethod == false</c> (there is no
    /// classic form for `int.MakeZero()`), so only the nested extension type exposes it.
    /// </summary>
    [Fact]
    public void StaticExtensionMethodInBlock_IsReportedAndMarkedStatic()
    {
        var makeZero = Assert.Single(Run("int"), i => i.Name == "MakeZero");

        Assert.Equal("method", makeZero.Kind);
        Assert.Equal("Demo.BlockExtensions", makeZero.DeclaringType);
        Assert.Equal("source", makeZero.Origin);
        Assert.True(makeZero.IsStatic);
    }

    /// <summary>
    /// An instance block method appears TWICE in the symbol model — lifted onto the container with
    /// IsExtensionMethod set, and again inside the nested extension type. Both passes can see it,
    /// so it is exactly the member a naive "collect methods in pass B too" fix duplicates.
    /// </summary>
    [Fact]
    public void BlockInstanceMethod_IsReportedExactlyOnce()
    {
        Assert.Equal(1, Run("int").Count(i => i.Name == "Thrice"));
    }

    [Fact]
    public void BlockPropertyAccessors_AreNotReportedAsMethods()
    {
        Assert.DoesNotContain("get_Tripled", Names(Run("int")));
        Assert.DoesNotContain("get_Zero", Names(Run("int")));
    }

    // ------------------------------------------------------------------ receiver shapes

    [Fact]
    public void ArrayReceiver_IsSupported()
    {
        var results = Run("string[]");

        Assert.Contains("Firsty", Names(results));
        // Arrays are IEnumerable<T>, so BCL LINQ applies too — the real reason someone asks.
        Assert.Contains("Where", Names(results));
        Assert.DoesNotContain("Doubled", Names(results));
    }

    [Fact]
    public void NullableReceiver_IsSupported()
    {
        var results = Run("int?");

        Assert.Contains("OrZero", Names(results));
        Assert.DoesNotContain("Doubled", Names(results));
    }

    [Fact]
    public void TupleReceiver_IsSupported()
    {
        Assert.Contains("Combine", Names(Run("(int, string)")));
    }

    [Fact]
    public void NullableReferenceReceiver_ResolvesToTheUnderlyingType()
    {
        // `string?` is `string`; there is no Nullable<string>.
        Assert.Contains("First2", Names(Run("string?")));
    }

    // ------------------------------------------------------------------ signature shape

    /// <summary>
    /// Methods and properties render the same way — leading return type, then the call-site form.
    /// A tool that prints `int Tripled` beside `Thrice()` reads as if one of them is broken.
    /// </summary>
    [Fact]
    public void Signature_LeadsWithTheReturnType_ForBothKinds()
    {
        var results = Run("int");

        Assert.Equal("int Tripled", Assert.Single(results, i => i.Name == "Tripled").Signature);
        Assert.Equal("int Thrice()", Assert.Single(results, i => i.Name == "Thrice").Signature);
        Assert.Equal("int Doubled()", Assert.Single(results, i => i.Name == "Doubled").Signature);
        Assert.Equal("int Zero", Assert.Single(results, i => i.Name == "Zero").Signature);
    }

    [Fact]
    public void Signature_ShowsInferredTypeArguments()
    {
        var where = Run("List<int>").First(i => i.Name == "Where");

        Assert.StartsWith("IEnumerable<int> Where<int>(", where.Signature);
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

    /// <summary>
    /// Genuinely cross-project control for <see cref="UnreferencedProjectExtension_IsNotReported"/>:
    /// the extension lives in <c>Core</c>, the receiver type in <c>App</c>, and App references Core.
    /// This is the branch that walks <c>ReferencedAssemblySymbols</c> rather than the receiver
    /// compilation's own types — a same-project control never reaches it. It also pins the
    /// public-only filter applied there: Core's internal container must stay invisible.
    /// </summary>
    [Fact]
    public void ReferencedProjectExtension_IsReported()
    {
        const string core = """
            namespace Core;

            public interface IWidget { }

            public static class WidgetExtensions
            {
                public static int Boost(this IWidget widget) => 1;
            }

            internal static class InternalWidgetExtensions
            {
                public static int Hidden(this IWidget widget) => 1;
            }
            """;
        const string app = """
            namespace App;

            using Core;

            public class Widget : IWidget { }
            """;

        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("Core", [("Core.cs", core)]),
            ("App", [("App.cs", app)]));

        var results = GetExtensionMethodsLogic.Execute(
            loaded, resolver, new MetadataSymbolResolver(loaded, resolver), "Widget", null);

        Assert.Contains("Boost", Names(results));
        Assert.DoesNotContain("Hidden", Names(results));
    }

    /// <summary>
    /// A metadata receiver such as <c>string</c> is declared by no project in the solution, so
    /// there is no "the receiver's compilation" to scope candidates to. Any project's extension on
    /// <c>string</c> is a legitimate answer to "what can I call on a string?", and picking one
    /// compilation silently drops the rest.
    /// </summary>
    [Fact]
    public void MetadataReceiver_ReportsExtensionsFromEveryProject()
    {
        const string plain = "namespace Aaa; public class Unrelated { }";
        const string extensions = """
            namespace Zzz;

            public static class StringExtensions
            {
                public static string Slugify(this string value) => value;
            }
            """;

        // "AaaPlain" is added first, so it does NOT reference "ZzzExtensions" — and it sorts first
        // by assembly name, which is exactly the compilation a first-match fallback would pick.
        var (loaded, resolver) = RenameTestWorkspace.Create(
            RenameTestWorkspaceOptions.FrameworkReferences,
            ("AaaPlain", [("Unrelated.cs", plain)]),
            ("ZzzExtensions", [("StringExtensions.cs", extensions)]));

        var results = GetExtensionMethodsLogic.Execute(
            loaded, resolver, new MetadataSymbolResolver(loaded, resolver), "string", null);

        Assert.Contains("Slugify", Names(results));
        // Both compilations reference the same BCL, so every LINQ member is found twice; scanning
        // more than one compilation is only correct if the results are deduplicated.
        Assert.Contains("Where", Names(results));
        Assert.Equal(results.Count, results.Distinct().Count());
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
