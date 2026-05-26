using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetTypeHierarchyToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public GetTypeHierarchyToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void GetHierarchy_ForGreeter_ShowsBaseAndDerived()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "Greeter");

        Assert.NotNull(result);
        Assert.Contains(result.Interfaces, i => i.FullName.Contains("IGreeter", StringComparison.Ordinal));
        Assert.Contains(result.Derived, d => d.FullName.Contains("FancyGreeter", StringComparison.Ordinal));
        Assert.Equal("source", result.Origin?.Kind);
    }

    [Fact]
    public void GetHierarchy_ForGreeter_HasNoBases()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "Greeter");

        Assert.NotNull(result);
        Assert.Empty(result.Bases);
    }

    [Fact]
    public void GetHierarchy_ForFancyGreeter_ShowsGreeterAsBase()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "FancyGreeter");

        Assert.NotNull(result);
        Assert.Contains(result.Bases, b => b.FullName.Contains("Greeter", StringComparison.Ordinal));
        Assert.Empty(result.Derived);
    }

    [Fact]
    public void GetHierarchy_ForUnknownType_ThrowsSymbolNotFound()
    {
        var ex = Assert.Throws<McpToolException>(() =>
            GetTypeHierarchyLogic.Execute(_resolver, _metadata, "NonExistentType"));

        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }

    [Fact]
    public void TypeHierarchy_MetadataInterface_HasOrigin()
    {
        var result = GetTypeHierarchyLogic.Execute(_resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotNull(result);
        Assert.Equal("metadata", result!.Origin?.Kind);
        // IServiceCollection : IList<ServiceDescriptor>, ICollection<ServiceDescriptor>, ...
        // -- the base-interface chain is present.
        Assert.NotEmpty(result.Interfaces);
    }
}
