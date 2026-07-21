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

    // ------------------------------------------------------------------ required members

    private const string RequiredSource = """
        namespace Demo;

        public class Req
        {
            public required int A { get; init; }
            public string B { get; init; }
            public int C { get; set; }
        }

        public class BaseReq { public required string Name { get; init; } }
        public class DerivedReq : BaseReq { public required int Age { get; init; } }
        """;

    private static InstantiationOptionsResult RunRequired(string symbol)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("R.cs", RequiredSource));
        return GetInstantiationOptionsLogic.Execute(loaded, resolver, symbol, null);
    }

    [Fact]
    public void Required_members_are_reported()
    {
        var m = Assert.Single(RunRequired("Req").RequiredMembers);

        Assert.Equal("A", m.Name);
        Assert.Equal("int", m.Type);
    }

    // ------------------------------------------------------------------ factories

    private const string FactorySource = """
        using System.Threading.Tasks;
        namespace Demo;

        public class Widget { internal Widget() {} }
        public static class WidgetFactory { public static Widget Create() => new(); }
        public class WidgetBuilder { public Widget Build() => new(); }

        public class FactoryOnly
        {
            private FactoryOnly() {}
            public static FactoryOnly Create() => new();
            public static Task<FactoryOnly> CreateAsync() => Task.FromResult(new FactoryOnly());
            public static FactoryOnly Instance { get; } = new();
            public static readonly FactoryOnly Default = new();
            public static int NotAFactory() => 1;
        }
        """;

    private static InstantiationOptionsResult RunFactories(string symbol)
    {
        var (loaded, resolver) = RenameTestWorkspace.Create(("F.cs", FactorySource));
        return GetInstantiationOptionsLogic.Execute(loaded, resolver, symbol, null);
    }

    [Fact]
    public void Finds_static_factory_declared_on_another_type()
    {
        var r = RunFactories("Widget");

        Assert.Contains(
            r.Factories,
            f => f.DeclaringType.EndsWith("WidgetFactory", StringComparison.Ordinal)
                 && f.Signature.Contains("Create", StringComparison.Ordinal));
    }

    [Fact]
    public void Instance_builder_methods_are_excluded()
    {
        // WidgetBuilder.Build() returns Widget but is an instance method: the builder itself
        // would need constructing, so it is deliberately not a construction option.
        Assert.DoesNotContain(
            RunFactories("Widget").Factories,
            f => f.Signature.Contains("Build", StringComparison.Ordinal));
    }

    /// <summary>
    /// `static FactoryOnly Instance { get; }` emits a static backing field of self type, which is
    /// a perfect structural match for a factory and something no caller can write.
    /// <para>
    /// Asserted as "Instance appears exactly once, as a property" rather than by hunting for
    /// <c>k__BackingField</c> in the signature: Roslyn 5.6 renders that field as
    /// <c>FactoryOnly Instance.field</c>, so a name-based assertion passes whether or not the
    /// filter exists and tests nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public void Compiler_generated_backing_field_is_not_a_factory()
    {
        var instance = Assert.Single(
            RunFactories("FactoryOnly").Factories,
            f => f.Signature.Contains("Instance", StringComparison.Ordinal));

        Assert.Equal("property", instance.Kind);
    }

    [Fact]
    public void Static_property_and_field_factories_are_reported()
    {
        var f = RunFactories("FactoryOnly").Factories;

        Assert.Contains(f, x => x.Kind == "property" && x.Signature.Contains("Instance", StringComparison.Ordinal));
        Assert.Contains(f, x => x.Kind == "field" && x.Signature.Contains("Default", StringComparison.Ordinal));
    }

    [Fact]
    public void Task_returning_factory_is_unwrapped_and_marked_async()
    {
        var f = Assert.Single(
            RunFactories("FactoryOnly").Factories,
            x => x.Signature.Contains("CreateAsync", StringComparison.Ordinal));

        Assert.True(f.IsAsync);
    }

    [Fact]
    public void Members_not_returning_the_type_are_excluded()
    {
        Assert.DoesNotContain(
            RunFactories("FactoryOnly").Factories,
            x => x.Signature.Contains("NotAFactory", StringComparison.Ordinal));
    }

    [Fact]
    public void Required_members_inherited_from_a_base_type_are_reported()
    {
        // A base type's required member must still be set by whoever constructs the derived type,
        // and GetMembers() on the derived type alone never mentions it.
        var names = RunRequired("DerivedReq").RequiredMembers.Select(m => m.Name).ToList();

        Assert.Contains("Age", names);
        Assert.Contains("Name", names);
    }
}
