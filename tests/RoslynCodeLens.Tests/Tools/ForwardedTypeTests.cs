using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// A framework type lives in a different assembly per target framework (System.IDisposable is in
/// mscorlib for net48, in netstandard for netstandard2.0 and in System.Runtime for net10.0), and
/// type forwarders make the three one logical type. Every query for a framework symbol must report
/// the usages of all three ForwardedTypesSolution projects, whichever instance the index keeps and
/// whichever one Roslyn's SymbolFinder starts from (#589).
/// </summary>
[Collection("ForwardedTypesSolution")]
public class ForwardedTypeTests
{
    private static readonly string[] Projects = ["Net10App", "Net48Lib", "NetStdLib"];

    private static readonly string[] ResourceTypes =
        ["Net10App.Net10AppResource", "Net48Lib.Net48LibResource", "NetStdLib.NetStdLibResource"];

    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public ForwardedTypeTests(ForwardedTypesSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindNamedTypes_FrameworkTypeSeenInThreeAssemblies_ReturnsOneType()
    {
        var types = _resolver.FindNamedTypes("System.IDisposable");

        Assert.Single(types);
    }

    [Fact]
    public void FindAttributeUsages_FrameworkAttributeByFullName_ReportsEveryProject()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "System.ObsoleteAttribute");

        Assert.Equal(Projects, results.Select(r => r.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    [Fact]
    public void FindImplementations_FrameworkInterface_ReportsEveryProject()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, _metadata, "System.IDisposable");

        Assert.Equal(ResourceTypes, SourceTypes(results), StringComparer.Ordinal);
    }

    [Fact]
    public void GetTypeHierarchy_FrameworkInterface_ReportsDerivedTypesOfEveryProject()
    {
        var hierarchy = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "System.IDisposable");

        Assert.Equal(ResourceTypes, SourceTypes(hierarchy.Derived), StringComparer.Ordinal);
    }

    [Fact]
    public void FindReferences_FrameworkType_ReportsEveryProject()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "System.IDisposable");

        Assert.Equal(Projects, results.Select(r => r.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    [Fact]
    public void FindReferences_FrameworkMethod_ReportsEveryProject()
    {
        var results = FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "System.Console.WriteLine");

        Assert.Equal(Projects, results.Select(r => r.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    [Fact]
    public void FindCallers_FrameworkMethod_ReportsEveryProject()
    {
        var results = FindCallersLogic.Execute(_loaded, _resolver, _metadata, "System.Console.WriteLine");

        Assert.Equal(
            ["Net10AppUsages.Write", "Net48LibUsages.Write", "NetStdLibUsages.Write"],
            results.Select(r => r.Caller).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.Equal(Projects, results.Select(r => r.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    [Fact]
    public void AnalyzeChangeImpact_FrameworkType_ReportsEveryProject()
    {
        var impact = AnalyzeChangeImpactLogic.Execute(_loaded, _resolver, _metadata, "System.IDisposable");

        Assert.NotNull(impact);
        Assert.Equal(Projects, impact.AffectedProjects, StringComparer.Ordinal);
    }

    [Fact]
    public void FindMethods_OverloadMissingOnFirstScannedFramework_ReturnsOverloadsOfEveryFramework()
    {
        // net48 keeps its System.String as the indexed instance; it lacks the char and StringComparison overloads.
        var resolver = ResolverScanningNet48First();

        var methods = resolver.FindMethods("System.String.Contains");

        Assert.Equal(
            [
                "string.Contains(char)",
                "string.Contains(char, System.StringComparison)",
                "string.Contains(string)",
                "string.Contains(string, System.StringComparison)",
            ],
            methods.Select(m => m.ToDisplayString()).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    [Fact]
    public void FindCallers_OverloadMissingOnFirstScannedFramework_ReportsCaller()
    {
        // Only Net10App calls String.Contains, through an overload the net48 instance lacks.
        var results = FindCallersLogic.Execute(_loaded, ResolverScanningNet48First(), _metadata, "System.String.Contains");

        var caller = Assert.Single(results);
        Assert.Equal("Net10AppUsages.Mentions", caller.Caller);
        Assert.Equal("Net10App", caller.Project);
    }

    [Fact]
    public void FindReferences_OverloadMissingOnFirstScannedFramework_ReportsReference()
    {
        var results = FindReferencesLogic.Execute(_loaded, ResolverScanningNet48First(), _metadata, "System.String.Contains");

        var reference = Assert.Single(results);
        Assert.Equal("Net10App", reference.Project);
        Assert.EndsWith("Net10AppUsages.cs", reference.File, StringComparison.Ordinal);
    }

    [Fact]
    public void FindObsoleteUsage_AttributeMissingOnFirstScannedFramework_ReportsUsage()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, ResolverScanningNet48First(), project: null, errorOnly: false);

        var group = Assert.Single(result.Groups);
        Assert.Equal("string.Copy(string)", group.SymbolName);
        var usage = Assert.Single(group.Usages);
        Assert.Equal("Net10AppUsages.Clone", usage.CallerName);
        Assert.Equal("Net10App", usage.Project);
        Assert.Equal("string.Copy(text)", usage.Snippet);
    }

    // The resolver scans the projects in file path order, Net10App first. Moving Net48Lib to a
    // directory that sorts first makes its framework instances the indexed ones.
    private SymbolResolver ResolverScanningNet48First()
    {
        var net48 = _loaded.Solution.Projects.Single(p => string.Equals(p.Name, "Net48Lib", StringComparison.Ordinal));
        var solutionDir = Path.GetDirectoryName(Path.GetDirectoryName(net48.FilePath))!;
        var solution = _loaded.Solution.WithProjectFilePath(net48.Id, Path.Combine(solutionDir, "0", "Net48Lib.csproj"));

        return new SymbolResolver(new LoadedSolution { Solution = solution, Compilations = _loaded.Compilations });
    }

    // Framework types that implement the interface have no project; only the fixture's own types do.
    private static IEnumerable<string> SourceTypes(IEnumerable<RoslynCodeLens.Models.SymbolLocation> locations)
        => locations.Where(l => l.Project.Length > 0).Select(l => l.FullName).Order(StringComparer.Ordinal);
}
