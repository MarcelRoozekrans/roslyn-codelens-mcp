using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Per-occurrence reporting: references are keyed by (file, line, column), so several
/// references on one line survive as distinct items with their own kinds.
/// </summary>
public class FindReferencesLogicTests
{
    private static IReadOnlyList<SymbolReference> Find(string source, string symbol)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Sample.cs", source));
        var metadata = new MetadataSymbolResolver(loaded, resolver);
        return FindReferencesLogic.Execute(loaded, resolver, metadata, symbol);
    }

    [Fact]
    public void SameLineMultiRef_YieldsTwoItems()
    {
        const string source = "class C { int _x; void M() { _x = _x + 1; } }";

        var results = Find(source, "C._x");

        var onLineOne = results.Where(r => r.Line == 1).ToList();
        Assert.Equal(2, onLineOne.Count);
        Assert.Equal(2, onLineOne.Select(r => r.Column).Distinct().Count());
    }

    [Fact]
    public void Write_And_Read_Classified()
    {
        const string source = "class C { int _x; void M() { _x = _x + 1; } }";

        var results = Find(source, "C._x").OrderBy(r => r.Column).ToList();

        Assert.Equal(["write", "read"], results.Select(r => r.ReferenceKind));
        Assert.True(results[0].Column < results[1].Column);
    }

    [Fact]
    public void Invocation_Classified()
    {
        const string source = "class C { void M() { } void U() { M(); } }";

        var results = Find(source, "C.M");

        Assert.Contains(results, r => string.Equals(r.ReferenceKind, "invocation", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeReference_Kinds()
    {
        const string source = """
            class Foo { }
            class Bar : Foo { }
            class C
            {
                void U()
                {
                    var f = new Foo();
                    var t = typeof(Foo);
                }
            }
            """;

        var kinds = Find(source, "Foo").Select(r => r.ReferenceKind).ToList();

        Assert.Contains("base_type", kinds, StringComparer.Ordinal);
        Assert.Contains("object_creation", kinds, StringComparer.Ordinal);
        Assert.Contains("typeof", kinds, StringComparer.Ordinal);
    }

    [Fact]
    public void Column_IsOneBased()
    {
        // "class C { int _x; void M() { _x = 1; } }"
        //                               ^ index 29 (0-based) -> column 30
        const string source = "class C { int _x; void M() { _x = 1; } }";

        var write = Assert.Single(
            Find(source, "C._x"), r => string.Equals(r.ReferenceKind, "write", StringComparison.Ordinal));

        Assert.Equal(source.IndexOf("_x = 1", StringComparison.Ordinal) + 1, write.Column);
    }
}
