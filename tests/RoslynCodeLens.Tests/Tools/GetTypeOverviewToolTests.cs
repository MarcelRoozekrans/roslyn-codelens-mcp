using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetTypeOverviewToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public GetTypeOverviewToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void Execute_ForGreeter_ReturnsFullOverview()
    {
        var result = GetTypeOverviewLogic.Execute(_loaded, _resolver, _metadata, "Greeter");

        Assert.NotNull(result);
        Assert.NotNull(result.Context);
        Assert.NotNull(result.Hierarchy);
        Assert.NotEmpty(result.Context.PublicMembers);
        Assert.NotEmpty(result.Hierarchy.Interfaces);
        Assert.Equal("source", result.Origin?.Kind);
    }

    [Fact]
    public void Execute_ForUnknownType_ThrowsSymbolNotFound()
    {
        var ex = Assert.Throws<McpToolException>(() =>
            GetTypeOverviewLogic.Execute(_loaded, _resolver, _metadata, "NonExistentType99"));

        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }

    [Fact]
    public void TypeOverview_MetadataInterface_ReturnsShapeWithOrigin()
    {
        var result = GetTypeOverviewLogic.Execute(_loaded, _resolver, _metadata,
            "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotNull(result);
        Assert.Equal("metadata", result!.Origin?.Kind);
        Assert.NotNull(result.Context);
        Assert.NotEmpty(result.Context!.PublicMembers);
        Assert.Empty(result.Diagnostics);
    }
}
