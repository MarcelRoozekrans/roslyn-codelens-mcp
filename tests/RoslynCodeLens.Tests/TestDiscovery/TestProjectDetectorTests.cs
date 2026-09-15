using RoslynCodeLens;
using RoslynCodeLens.TestDiscovery;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.TestDiscovery;

[Collection("TestSolution")]
public class TestProjectDetectorTests
{
    private readonly LoadedSolution _loaded;

    public TestProjectDetectorTests(TestSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
    }

    [Fact]
    public void GetTestProjectIds_DetectsXUnitNUnitAndMSTest()
    {
        var ids = TestProjectDetector.GetTestProjectIds(_loaded.Solution);

        var names = ids
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Contains("NUnitFixture", names);
        Assert.Contains("MSTestFixture", names);
    }

    [Fact]
    public void GetTestProjectIds_ExcludesNonTestProjects()
    {
        var ids = TestProjectDetector.GetTestProjectIds(_loaded.Solution);

        var names = ids
            .Select(id => _loaded.Solution.GetProject(id)!.Name)
            .ToList();

        Assert.DoesNotContain("TestLib", names);
        Assert.DoesNotContain("TestLib2", names);
    }

    [Theory]
    [InlineData("xunit.core")] // xUnit v2
    [InlineData("xunit.assert")]
    [InlineData("xunit.v3.core")] // xUnit v3
    [InlineData("nunit.framework")]
    [InlineData("Microsoft.VisualStudio.TestPlatform.TestFramework")] // MSTest
    public void IsTestFrameworkAssembly_RecognizesFrameworkAssemblyNames(string assemblyName)
    {
        Assert.True(TestProjectDetector.IsTestFrameworkAssembly(assemblyName));
    }

    [Theory]
    [InlineData("System.Runtime")]
    [InlineData("Microsoft.Extensions.DependencyInjection")]
    [InlineData("Microsoft.VisualStudio.Threading")]
    [InlineData("FluentAssertions")]
    public void IsTestFrameworkAssembly_IgnoresNonFrameworkAssemblyNames(string assemblyName)
    {
        Assert.False(TestProjectDetector.IsTestFrameworkAssembly(assemblyName));
    }
}
