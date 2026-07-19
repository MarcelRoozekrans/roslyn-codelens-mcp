using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class GetMethodSourceLogicTests
{
    private const string DemoSource = """
        using System;
        namespace Demo;

        public class Widget
        {
            static Widget() { }
            public Widget(string name) { }

            /// <summary>Adds one.</summary>
            [Obsolete("use ComputeV2")]
            public int Compute(int value) => value + 1;
            public int Compute(int a, int b)
            {
                return a + b;
            }

            public string Name { get; set; } = "w";
            public int _count = 42;
            public event EventHandler? Changed;
        }

        public partial class Split { public partial void Run(); }
        public partial class Split { public partial void Run() { } }
        """;

    private static IReadOnlyList<MemberSourceInfo> Execute(params string[] symbols)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Demo.cs", DemoSource));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        return GetMethodSourceLogic.Execute(resolver, metadata, symbols);
    }

    [Fact]
    public void XmlDocAndAttribute_IncludedInSource()
    {
        var items = Execute("Widget.Compute");

        var first = items[0];
        Assert.Equal("ok", first.Status);
        Assert.NotNull(first.Source);
        Assert.Contains("/// <summary>Adds one.</summary>", first.Source, StringComparison.Ordinal);
        Assert.Contains("[Obsolete(\"use ComputeV2\")]", first.Source, StringComparison.Ordinal);
        Assert.Contains("=> value + 1;", first.Source, StringComparison.Ordinal);

        // StartLine must point at the first line actually present in Source —
        // here the XML doc line, not the method's first token.
        var lines = DemoSource.Split('\n');
        var xmlDocLine = Array.FindIndex(lines,
            l => l.Contains("/// <summary>Adds one.</summary>", StringComparison.Ordinal)) + 1;
        Assert.True(xmlDocLine > 0, "XML doc line not found in DemoSource");
        Assert.Equal(xmlDocLine, first.StartLine);
    }

    [Fact]
    public void OverloadGroup_ExpandsToAdjacentItems()
    {
        var items = Execute("Widget.Compute");

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("ok", i.Status));
        Assert.All(items, i => Assert.Equal("method", i.Kind));
        Assert.Contains("int a, int b", items[1].Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Property_ReturnsWholeDeclaration()
    {
        var item = Assert.Single(Execute("Widget.Name"));

        Assert.Equal("ok", item.Status);
        Assert.Equal("property", item.Kind);
        Assert.Contains("{ get; set; }", item.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_ReturnsDeclarationStatement()
    {
        var item = Assert.Single(Execute("Widget._count"));

        Assert.Equal("ok", item.Status);
        Assert.Equal("field", item.Kind);
        Assert.Contains("public int _count = 42;", item.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Event_Returns()
    {
        var item = Assert.Single(Execute("Widget.Changed"));

        Assert.Equal("ok", item.Status);
        Assert.Equal("event", item.Kind);
        Assert.Contains("event EventHandler? Changed;", item.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void CtorRequestForm_ReturnsInstanceAndStatic()
    {
        var items = Execute("Widget.Widget");

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("ok", i.Status));
        Assert.All(items, i => Assert.Equal("constructor", i.Kind));
        Assert.Contains(items, i => i.Source!.Contains("string name", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Source!.Contains("static Widget()", StringComparison.Ordinal));
    }

    [Fact]
    public void CtorRequest_AmbiguousAcrossNamespaces_SingleAmbiguousItem()
    {
        const string firstSource = """
            namespace First;
            public class Dup { public Dup(int x) { } }
            """;
        const string secondSource = """
            namespace Second;
            public class Dup { public Dup(string s) { } }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("First.cs", firstSource), ("Second.cs", secondSource));
        var metadata = new MetadataSymbolResolver(loaded, resolver);

        var item = Assert.Single(GetMethodSourceLogic.Execute(resolver, metadata, ["Dup.Dup"]));

        Assert.Equal("ambiguous", item.Status);
        Assert.Null(item.Source);
        Assert.NotNull(item.Candidates);
        Assert.Equal(2, item.Candidates!.Count);
        Assert.Contains("First.Dup", item.Candidates);
        Assert.Contains("Second.Dup", item.Candidates);
    }

    [Fact]
    public void CtorRequest_MetadataType_SingleMetadataItem()
    {
        var item = Assert.Single(Execute("System.Text.StringBuilder.StringBuilder"));

        Assert.Equal("metadata", item.Status);
        Assert.Equal("constructor", item.Kind);
        Assert.Null(item.Source);
        Assert.NotNull(item.Note);
    }

    [Fact]
    public void CtorRequest_ImplicitCtorsOnly_NotFoundWithNote()
    {
        const string source = """
            namespace Plainville;
            public class Plain { public void M() { } }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(("Plain.cs", source));
        var metadata = new MetadataSymbolResolver(loaded, resolver);

        var item = Assert.Single(GetMethodSourceLogic.Execute(resolver, metadata, ["Plain.Plain"]));

        Assert.Equal("notFound", item.Status);
        Assert.Null(item.Source);
        Assert.NotNull(item.Note);
        Assert.Contains("compiler-generated", item.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialMethod_OneItemPerPart()
    {
        var items = Execute("Split.Run");

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("ok", i.Status));
        Assert.NotEqual(items[0].Source, items[1].Source);
        Assert.Contains(items, i => i.Source!.Contains("Run();", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Source!.Contains("Run() { }", StringComparison.Ordinal));
    }

    [Fact]
    public void NotFound_PerItem()
    {
        var items = Execute("Widget.Nope", "Widget.Name");

        Assert.Equal(2, items.Count);
        Assert.Equal("Widget.Nope", items[0].RequestedSymbol);
        Assert.Equal("notFound", items[0].Status);
        Assert.Null(items[0].Source);
        Assert.Equal("Widget.Name", items[1].RequestedSymbol);
        Assert.Equal("ok", items[1].Status);
    }

    [Fact]
    public void Ambiguous_ListsCandidates()
    {
        const string firstSource = """
            namespace First;
            public class Dup { public void Go() { } }
            """;
        const string secondSource = """
            namespace Second;
            public class Dup { public void Go() { } }
            """;
        var (loaded, resolver) = RenameTestWorkspace.Create(
            ("First.cs", firstSource), ("Second.cs", secondSource));
        var metadata = new MetadataSymbolResolver(loaded, resolver);

        var item = Assert.Single(GetMethodSourceLogic.Execute(resolver, metadata, ["Dup.Go"]));

        Assert.Equal("ambiguous", item.Status);
        Assert.Null(item.Source);
        Assert.NotNull(item.Candidates);
        Assert.Equal(2, item.Candidates!.Count);
    }

    [Fact]
    public void Metadata_Status_OneItemPerRequest()
    {
        // String.Concat has ~16 overloads; a metadata-only logical group must
        // collapse to ONE item for the request, not one stub per overload.
        var item = Assert.Single(Execute("System.String.Concat"));

        Assert.Equal("metadata", item.Status);
        Assert.Null(item.Source);
        Assert.NotNull(item.Note);
    }

    [Fact]
    public void Metadata_Item_CarriesOrigin()
    {
        var item = Assert.Single(Execute("System.String.Concat"));

        Assert.NotNull(item.Origin);
        Assert.NotNull(item.Origin!.AssemblyName);
    }

    [Fact]
    public void MetadataType_ViaMetadataNameFallback_UnsupportedKind()
    {
        // The backtick metadata-name form misses the source index and resolves via
        // Compilation.GetTypeByMetadataName — a TYPE must not surface as a
        // kind='method' metadata item.
        var item = Assert.Single(Execute("System.Collections.Generic.List`1"));

        Assert.Equal("unsupportedKind", item.Status);
        Assert.Null(item.Kind);
        Assert.Null(item.Source);
        Assert.NotNull(item.Note);
    }

    [Fact]
    public void TypeName_UnsupportedKind()
    {
        var item = Assert.Single(Execute("Widget"));

        Assert.Equal("unsupportedKind", item.Status);
        Assert.Null(item.Source);
    }

    [Fact]
    public void EmptyInput_Throws()
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Demo.cs", DemoSource));
        var metadata = new MetadataSymbolResolver(loaded, resolver);

        var ex = Assert.Throws<McpToolException>(
            () => GetMethodSourceLogic.Execute(resolver, metadata, []));
        Assert.Equal(ToolErrorCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public void LineSpan_IsAccurate()
    {
        var item = Assert.Single(Execute("Widget.Name"));

        // 1-based line of the property declaration inside DemoSource.
        var lines = DemoSource.Split('\n');
        var propertyLine = Array.FindIndex(lines,
            l => l.Contains("public string Name", StringComparison.Ordinal)) + 1;
        Assert.True(propertyLine > 0, "property line not found in DemoSource");

        Assert.Equal("Demo.cs", item.File);
        // The emitted Source is exactly the single property line, so both ends of
        // the span must be that line.
        Assert.Equal(propertyLine, item.StartLine);
        Assert.Equal(propertyLine, item.EndLine);
    }
}
