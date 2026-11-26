using Microsoft.CodeAnalysis;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Symbols;

/// <summary>
/// Each ForwardedTypesSolution project sees a framework type in a different assembly (mscorlib,
/// netstandard, System.Runtime); the map built over all of them must give every instance the same
/// canonical assembly name, whichever compilation it scans first.
/// </summary>
[Collection("ForwardedTypesSolution")]
public class TypeForwardingMapTests
{
    private readonly LoadedSolution _loaded;

    public TypeForwardingMapTests(ForwardedTypesSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
    }

    // Net48Lib first is the order that matters: net48's mscorlib has the same identity as the mscorlib facade of
    // net10.0, but only the facade forwards the framework types.
    public static TheoryData<string, string> FrameworkTypes => new()
    {
        { "System.IDisposable", "Net48Lib" },
        { "System.IDisposable", "NetStdLib" },
        { "System.IDisposable", "Net10App" },
        // Nested: forwarders only name the outermost type.
        { "System.Environment+SpecialFolder", "Net48Lib" },
        { "System.Environment+SpecialFolder", "NetStdLib" },
        { "System.Environment+SpecialFolder", "Net10App" },
    };

    [Theory]
    [MemberData(nameof(FrameworkTypes))]
    public void GetCanonicalAssemblyName_FrameworkTypeOfEveryTargetFramework_ReturnsOneName(string metadataName, string firstProject)
    {
        var compilations = CompilationsStartingWith(firstProject);
        var map = TypeForwardingMap.Build(compilations);
        var instances = compilations.Select(c => c.GetTypeByMetadataName(metadataName)!).ToList();

        Assert.Equal(
            ["System.Runtime", "mscorlib", "netstandard"],
            instances.Select(t => t.ContainingAssembly.Identity.Name).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
        Assert.All(instances, t => Assert.Equal("System.Runtime", map.GetCanonicalAssemblyName(t)));
    }

    [Fact]
    public void GetCanonicalAssemblyName_TypeNoForwarderMentions_ReturnsItsOwnAssemblyName()
    {
        var compilations = CompilationsStartingWith("Net48Lib");
        var map = TypeForwardingMap.Build(compilations);
        var type = compilations[0].GetTypeByMetadataName("Net48Lib.Net48LibResource")!;

        Assert.Equal("Net48Lib", map.GetCanonicalAssemblyName(type));
    }

    private List<Compilation> CompilationsStartingWith(string projectName)
    {
        return _loaded.Solution.Projects
            .OrderBy(p => string.Equals(p.Name, projectName, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => _loaded.Compilations[p.Id])
            .ToList();
    }
}