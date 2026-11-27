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

    // Common part of the [Obsolete] messages of AppDomain.GetCurrentThreadId() on net10.0 and net48.
    private const string GetCurrentThreadIdMessage =
        "AppDomain.GetCurrentThreadId has been deprecated because it does not provide a stable Id when managed threads are running " +
        "on fibers (aka lightweight threads). To get a stable identifier for a managed thread, use the ManagedThreadId property on Thread";

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

        var group = Assert.Single(result.Groups, g => g.SymbolName == "string.Copy(string)");
        var usage = Assert.Single(group.Usages);
        Assert.Equal("Net10AppUsages.Clone", usage.CallerName);
        Assert.Equal("Net10App", usage.Project);
        Assert.Equal("string.Copy(text)", usage.Snippet);
    }

    [Fact]
    public void FindObsoleteUsage_CallBoundToNonObsoleteFrameworkInstance_IsNotReported()
    {
        // Net48Lib calls String.Copy too, but its net48 instance is not [Obsolete]; the compiler does not warn there.
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g => g.SymbolName == "string.Copy(string)");
        var usage = Assert.Single(group.Usages);
        Assert.Equal("Net10AppUsages.Clone", usage.CallerName);
        Assert.Equal("Net10App", usage.Project);
    }

    [Fact]
    public void FindObsoleteUsage_ConstructorOfTypeNotObsoleteOnItsFramework_IsNotReported()
    {
        // Both projects call new SHA1Managed(); only the net10.0 type is [Obsolete], its constructor is not.
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups, g => g.SymbolName == "System.Security.Cryptography.SHA1Managed");
        var usage = Assert.Single(group.Usages);
        Assert.Equal("Net10AppUsages.Hasher", usage.CallerName);
        Assert.Equal("Net10App", usage.Project);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindObsoleteUsage_InstancesWithDifferentMessages_ReportsGroupPerMessage(bool scanNet48First)
    {
        // Each usage carries the message of the instance it binds to, whichever instance the index scanned first.
        // net48 and netstandard2.0 share their message and therefore one group.
        var resolver = scanNet48First ? ResolverScanningNet48First() : _resolver;

        var result = FindObsoleteUsageLogic.Execute(_loaded, resolver, project: null, errorOnly: false);

        // Equal severity and name: the usage count orders the two groups.
        var groups = result.Groups.Where(g => g.SymbolName == "System.AppDomain.GetCurrentThreadId()");
        Assert.Collection(
            groups,
            net48 =>
            {
                Assert.Equal(GetCurrentThreadIdMessage + ".  http://go.microsoft.com/fwlink/?linkid=14202", net48.DeprecationMessage);
                Assert.False(net48.IsError);
                Assert.Equal(3, net48.UsageCount);
                Assert.Collection(
                    net48.Usages,
                    u => Assert.Equal("Net48LibUsages.ThreadId", u.CallerName),
                    u => Assert.Equal("NetStdLibUsages.ThreadId", u.CallerName),
                    u => Assert.Equal("SharedUsages.ThreadId", u.CallerName));
            },
            net10 =>
            {
                Assert.Equal(GetCurrentThreadIdMessage + " instead.", net10.DeprecationMessage);
                Assert.False(net10.IsError);
                Assert.Equal(2, net10.UsageCount);
                Assert.Collection(
                    net10.Usages,
                    u => Assert.Equal("Net10AppUsages.ThreadId", u.CallerName),
                    u => Assert.Equal("SharedUsages.ThreadId", u.CallerName));
            });
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
