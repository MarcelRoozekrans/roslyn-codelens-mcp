using RoslynCodeLens;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests;

public class SymbolResolverTests : IAsyncLifetime
{
    private LoadedSolution _loaded = null!;

    public async Task InitializeAsync()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "TestSolution", "TestSolution.slnx"));
        _loaded = await new SolutionLoader().LoadAsync(fixturePath).ConfigureAwait(false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void FindBySimpleName_ReturnsMatches()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindNamedTypes("Greeter");

        Assert.Contains(results, s => string.Equals(s.Name, "Greeter", StringComparison.Ordinal));
    }

    [Fact]
    public void FindByFullName_ReturnsExactMatch()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindNamedTypes("TestLib.Greeter");

        Assert.Single(results);
        Assert.Equal("TestLib.Greeter", results[0].ToDisplayString());
    }

    [Fact]
    public void FindMethods_ReturnsBySymbolName()
    {
        var resolver = new SymbolResolver(_loaded);
        var results = resolver.FindMethods("Greeter.Greet");

        Assert.NotEmpty(results);
    }

    private const string GenericRepositorySource = """
        namespace Data;

        public class Repository<T>
        {
            public T? GetById(int id) => default;
        }
        """;

    [Fact]
    public void FindSymbols_QualifiedNameWithoutTypeParameters_FindsGenericType()
    {
        var (_, resolver) = RenameTestWorkspace.Create(("Repository.cs", GenericRepositorySource));

        var results = resolver.FindSymbols("Data.Repository");

        var symbol = Assert.Single(results);
        Assert.Equal("Data.Repository<T>", symbol.ToDisplayString());
    }

    [Fact]
    public void FindNamedTypes_StrippedName_ReturnsAllArities()
    {
        var (_, resolver) = RenameTestWorkspace.Create(
            ("Repository1.cs", GenericRepositorySource),
            ("Repository2.cs", """
                namespace Data;

                public class Repository<T1, T2>
                {
                    public T1? GetById(T2 key) => default;
                }
                """));

        var results = resolver.FindNamedTypes("Data.Repository");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, t => t.ToDisplayString() == "Data.Repository<T>");
        Assert.Contains(results, t => t.ToDisplayString() == "Data.Repository<T1, T2>");
    }

    [Fact]
    public void FindSymbols_MemberOfGenericType_ResolvesViaStrippedTypeName()
    {
        var (_, resolver) = RenameTestWorkspace.Create(("Repository.cs", GenericRepositorySource));

        var results = resolver.FindSymbols("Data.Repository.GetById");

        var symbol = Assert.Single(results);
        Assert.Equal("GetById", symbol.Name);
        Assert.Equal("Data.Repository<T>", symbol.ContainingType.ToDisplayString());
    }

    [Fact]
    public void FindNamedTypes_ExactGenericDisplayName_StillWorks()
    {
        var (_, resolver) = RenameTestWorkspace.Create(("Repository.cs", GenericRepositorySource));

        var results = resolver.FindNamedTypes("Data.Repository<T>");

        var type = Assert.Single(results);
        Assert.Equal("Data.Repository<T>", type.ToDisplayString());
    }

    [Fact]
    public void FindNamedTypes_StrippedName_FindsNestedTypeInGenericOuter()
    {
        var (_, resolver) = RenameTestWorkspace.Create(("Outer.cs", """
            namespace Ns;

            public class Outer<T>
            {
                public class Inner
                {
                    public int Value;
                }
            }
            """));

        var results = resolver.FindNamedTypes("Ns.Outer.Inner");

        var type = Assert.Single(results);
        Assert.Equal("Ns.Outer<T>.Inner", type.ToDisplayString());
    }

    [Fact]
    public void FindNamedTypes_NonGenericDottedLookup_Unchanged()
    {
        var (_, resolver) = RenameTestWorkspace.Create(("Plain.cs", """
            namespace Data;

            public class PlainRepository
            {
                public int GetById(int id) => id;
            }
            """));

        var results = resolver.FindNamedTypes("Data.PlainRepository");

        var type = Assert.Single(results);
        Assert.Equal("Data.PlainRepository", type.ToDisplayString());
    }

    [Fact]
    public void IsGenerated_ReturnsFalse_ForRegularFiles()
    {
        var resolver = new SymbolResolver(_loaded);
        var types = resolver.FindNamedTypes("Greeter");
        Assert.NotEmpty(types);
        var (file, _) = resolver.GetFileAndLine(types[0]);
        Assert.NotEmpty(file);
        Assert.False(resolver.IsGenerated(file));
    }

    [Fact]
    public void IsGenerated_ReturnsTrue_ForObjPaths()
    {
        var resolver = new SymbolResolver(_loaded);
        Assert.True(resolver.IsGenerated(@"C:\project\obj\Debug\net10.0\Generated.cs"));
        Assert.True(resolver.IsGenerated(@"C:\project\obj\Release\net10.0\SomeGen.g.cs"));
    }

    [Fact]
    public void IsGenerated_ReturnsTrue_ForNullOrEmptyPaths()
    {
        var resolver = new SymbolResolver(_loaded);
        Assert.True(resolver.IsGenerated(""));
        Assert.True(resolver.IsGenerated(null!));
    }
}
