using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

[Collection("TestSolution")]
public class GetSymbolContextToolTests
{
    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public GetSymbolContextToolTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void GetContext_ForGreeterConsumer_ShowsInjectedDeps()
    {
        var result = GetSymbolContextLogic.Execute(_loaded, _resolver, _metadata, "GreeterConsumer");

        Assert.NotNull(result);
        Assert.Equal("TestLib2", result.Namespace);
        Assert.Contains(result.InjectedDependencies, d => d.Contains("IGreeter", StringComparison.Ordinal));
        Assert.Contains(result.PublicMembers, m => m.Contains("SayHello", StringComparison.Ordinal));
        Assert.Equal("source", result.Origin?.Kind);
    }

    [Fact]
    public void GetContext_ForUnknownType_ThrowsSymbolNotFound()
    {
        var ex = Assert.Throws<McpToolException>(() =>
            GetSymbolContextLogic.Execute(_loaded, _resolver, _metadata, "NonExistentType99"));

        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
    }

    [Fact]
    public void GetContext_MetadataInterface_ReturnsMembersAndOrigin()
    {
        var result = GetSymbolContextLogic.Execute(
            _loaded, _resolver, _metadata, "Microsoft.Extensions.DependencyInjection.IServiceCollection");

        Assert.NotNull(result);
        Assert.Equal("metadata", result!.Origin!.Kind);
        Assert.Equal("Microsoft.Extensions.DependencyInjection", result.Namespace);
        Assert.Empty(result.InjectedDependencies);
        Assert.NotEmpty(result.PublicMembers);
        Assert.Equal("", result.File);
        Assert.Equal(0, result.Line);
    }
}
