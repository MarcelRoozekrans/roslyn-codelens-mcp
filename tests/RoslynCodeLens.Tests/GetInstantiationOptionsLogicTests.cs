using RoslynCodeLens.Models;
using RoslynCodeLens.Tests.Fixtures;
using RoslynCodeLens.Tools;

namespace RoslynCodeLens.Tests;

public class GetInstantiationOptionsLogicTests
{
    private const string Source = """
        namespace Demo;

        public class Plain { public Plain() {} public Plain(int a) {} private Plain(bool b) {} }
        public record Rec(int A, string B);
        public struct S { public int X; }
        public abstract class Abs { protected Abs() {} }
        public static class Stat { }
        public interface IFoo { }
        public class Implicit { }
        """;

    private static InstantiationOptionsResult Run(string symbol, string? fromProject = null)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("Demo.cs", Source));
        return GetInstantiationOptionsLogic.Execute(loaded, resolver, symbol, fromProject);
    }

    [Fact]
    public void Reports_all_declared_constructors_with_accessibility()
    {
        var r = Run("Plain");

        Assert.True(r.Instantiable);
        Assert.Equal(3, r.Constructors.Count);
        Assert.Contains(r.Constructors, c => c.Accessibility == "private");
    }

    [Fact]
    public void Record_implicit_copy_constructor_is_excluded()
    {
        var r = Run("Rec");

        // Roslyn exposes a protected implicit Rec(Rec); it is never a construction option.
        Assert.DoesNotContain(
            r.Constructors, c => c.Parameters.Count == 1 && c.Parameters[0].Type.Contains("Rec", StringComparison.Ordinal));
        Assert.Contains(r.Constructors, c => c.Parameters.Count == 2);
    }

    [Fact]
    public void Struct_implicit_parameterless_constructor_is_reported()
    {
        var r = Run("S");

        var ctor = Assert.Single(r.Constructors);
        Assert.Empty(ctor.Parameters);
        Assert.True(ctor.IsImplicit);
    }

    [Fact]
    public void Class_with_no_declared_constructor_reports_implicit_one()
    {
        Assert.True(Assert.Single(Run("Implicit").Constructors).IsImplicit);
    }

    [Theory]
    [InlineData("Abs")]
    [InlineData("Stat")]
    [InlineData("IFoo")]
    public void Non_instantiable_types_report_no_constructors_and_a_note(string type)
    {
        var r = Run(type);

        Assert.False(r.Instantiable);
        Assert.Empty(r.Constructors);
        Assert.False(string.IsNullOrWhiteSpace(r.Note));
    }

    [Fact]
    public void Abstract_note_points_at_find_implementations()
    {
        Assert.Contains("find_implementations", Run("Abs").Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_symbol_throws_SymbolNotFound()
    {
        var ex = Assert.Throws<McpToolException>(() => Run("Nope"));
        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }
}
