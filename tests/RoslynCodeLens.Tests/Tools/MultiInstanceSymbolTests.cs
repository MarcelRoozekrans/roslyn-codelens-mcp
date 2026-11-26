using RoslynCodeLens;
using RoslynCodeLens.Models;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Tests.Fixtures;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// Tools must find usages no matter which symbol instance of a type the usage binds to. In
/// MultiInstanceSolution, AppNet9/TestsNet9 bind to the net9.0 instance of every Shared type and
/// AppNet10/TestsNet10 to the net10.0 instance, so a tool that compares symbols by instance
/// reports only one of the two consumers, whichever instance the index happened to keep.
/// Comparing by signature must not go so far as to merge symbols that only look alike: overloads,
/// unrelated events of the same name, and the per-framework instances of a test method whose
/// test attribute exists on one framework only (TestsMulti).
/// </summary>
[Collection("MultiInstanceSolution")]
public class MultiInstanceSymbolTests
{
    private static readonly string[] AppProjects = ["AppNet10", "AppNet9"];
    private static readonly string[] TestProjects = ["TestsNet10", "TestsNet9"];

    private readonly LoadedSolution _loaded;
    private readonly SymbolResolver _resolver;
    private readonly MetadataSymbolResolver _metadata;

    public MultiInstanceSymbolTests(MultiInstanceSolutionFixture fixture)
    {
        _loaded = fixture.Loaded;
        _resolver = fixture.Resolver;
        _metadata = fixture.Metadata;
    }

    [Fact]
    public void FindEventSubscribers_ConsumersOnDifferentTargetFrameworks_ReportsEverySubscription()
    {
        var results = FindEventSubscribersLogic.Execute(_loaded, _resolver, _metadata, "Shared.Publisher.Changed");

        Assert.Equal(AppProjects, results.Select(r => r.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.All(results, r => Assert.Equal(SubscriptionKind.Subscribe, r.Kind));
    }

    [Fact]
    public void FindEventSubscribers_InterfaceEventWithConsumersOnDifferentTargetFrameworks_ReportsEverySubscription()
    {
        var results = FindEventSubscribersLogic.Execute(_loaded, _resolver, _metadata, "Shared.INotifier.Notified");

        // Each app subscribes once through the interface and once through its implementing class.
        Assert.Equal(
            [
                ("AppNet10", "viaClass.Notified += OnChanged;"),
                ("AppNet10", "viaInterface.Notified += OnChanged;"),
                ("AppNet9", "viaClass.Notified += OnChanged;"),
                ("AppNet9", "viaInterface.Notified += OnChanged;"),
            ],
            results.Select(r => (r.Project, r.Snippet)).Order());
    }

    [Fact]
    public void FindEventSubscribers_SameNamedEventBesideExplicitInterfaceImplementation_IsNotReported()
    {
        // DualNotifier implements INotifier.Notified explicitly; its public Notified is another event.
        var results = FindEventSubscribersLogic.Execute(_loaded, _resolver, _metadata, "Shared.INotifier.Notified");

        Assert.DoesNotContain(results, r => string.Equals(r.Snippet, "dual.Notified += OnChanged;", StringComparison.Ordinal));
    }

    [Fact]
    public void FindTestsForSymbol_TestAttributeOnOneTargetFrameworkOnly_ReportsEveryTest()
    {
        var result = FindTestsForSymbolLogic.Execute(_loaded, _resolver, "Shared.Calculator.Halve");

        Assert.Equal(
            ["Halve_OnNet10", "Halve_OnNet9"],
            result.DirectTests.Select(t => t.FullyQualifiedName.Split('.')[^1]).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
        Assert.All(result.DirectTests, t => Assert.EndsWith("CalculatorTests.cs", t.FilePath, StringComparison.Ordinal));
    }

    [Fact]
    public void FindTestsForSymbol_TestsOnDifferentTargetFrameworks_ReportsEveryTest()
    {
        var result = FindTestsForSymbolLogic.Execute(_loaded, _resolver, "Shared.Publisher.Compute");

        Assert.Equal(TestProjects, result.DirectTests.Select(t => t.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.All(result.DirectTests, t =>
        {
            Assert.EndsWith("Compute_Doubles", t.FullyQualifiedName, StringComparison.Ordinal);
            Assert.EndsWith("PublisherTests.cs", t.FilePath, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void FindObsoleteUsage_CallersOnDifferentTargetFrameworks_ReportsEveryUsage()
    {
        var result = FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);

        var group = Assert.Single(result.Groups);
        Assert.Equal("Shared.Publisher.LegacyCompute(int)", group.SymbolName);
        // The calls to the ref overload are not usages of the obsolete one.
        Assert.Equal(2, group.UsageCount);
        Assert.Equal(AppProjects, group.Usages.Select(u => u.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.All(group.Usages, u => Assert.Equal("publisher.LegacyCompute(1)", u.Snippet));
    }

    [Fact]
    public void FindGodObjects_ReferencesFromDifferentTargetFrameworks_CountsEveryIncomingNamespace()
    {
        // Thresholds low enough that every non-test type is a size suspect; outgoing coupling is
        // out of reach so the incoming axis alone decides.
        var result = FindGodObjectsLogic.Execute(
            _loaded, _resolver, project: null,
            minLines: 1, minMembers: 1, minFields: 0, minIncomingNamespaces: 1, minOutgoingNamespaces: int.MaxValue);

        var publisher = Assert.Single(result.Types, g => string.Equals(g.TypeName, "Shared.Publisher", StringComparison.Ordinal));
        Assert.Equal(["AppNet10", "AppNet9", "TestsNet10", "TestsNet9"], publisher.SampleIncoming);
        Assert.Equal(4, publisher.IncomingNamespaces);
    }

    [Fact]
    public void FindAttributeUsages_FullNameWithUsagesOnDifferentTargetFrameworks_ReportsEveryUsage()
    {
        // The full name misses the simple-name fast path and compares the resolved attribute class.
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "Shared.MarkerAttribute");

        Assert.Equal(AppProjects, results.Select(r => r.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.All(results, r => Assert.EndsWith("Usages", r.TargetName, StringComparison.Ordinal));
    }

    [Fact]
    public void FindNamedTypes_FullNameDeclaredInTwoProjects_ReturnsBothTypesOrderedByProjectPath()
    {
        // Duplicate.Helper is declared in AppNet9 and in AppNet10: two distinct types that share a full name (#590).
        // The order must not depend on the load, so it follows the project paths: AppNet10/ sorts before AppNet9/.
        var types = _resolver.FindNamedTypes("Duplicate.Helper");

        Assert.Equal(
            ["RunNet10", "RunNet9"],
            types.SelectMany(t => t.GetMembers()).Where(m => m.Name.StartsWith("Run", StringComparison.Ordinal)).Select(m => m.Name),
            StringComparer.Ordinal);
    }

    [Fact]
    public void GoToDefinition_FullNameDeclaredInTwoProjects_ReportsBothDeclarations()
    {
        var results = GoToDefinitionLogic.Execute(_resolver, _metadata, "Duplicate.Helper");

        Assert.Equal(AppProjects, results.Select(r => r.Project).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    [Fact]
    public void FindImplementations_SameNamedImplementationsInTwoProjects_ReportsBoth()
    {
        var results = FindImplementationsLogic.Execute(_loaded, _resolver, _metadata, "Shared.IRunner");

        Assert.Equal(
            [("AppNet10", "Duplicate.Helper"), ("AppNet9", "Duplicate.Helper")],
            results.Select(r => (r.Project, r.FullName)).Order());
    }

    [Fact]
    public void GetTypeHierarchy_SameNamedDerivedTypesInTwoProjects_ReportsBoth()
    {
        var hierarchy = GetTypeHierarchyLogic.Execute(_resolver, _metadata, "Shared.IRunner");

        Assert.Equal(
            [("AppNet10", "Duplicate.Helper"), ("AppNet9", "Duplicate.Helper")],
            hierarchy.Derived.Select(r => (r.Project, r.FullName)).Order());
    }

    [Fact]
    public void FindAttributeUsages_FullNameDeclaredInTwoProjects_ReportsUsagesOfBoth()
    {
        var results = FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "Duplicate.MarkerAttribute");

        Assert.Equal(
            [("AppNet10", "Duplicate.Helper"), ("AppNet9", "Duplicate.Helper")],
            results.Select(r => (r.Project, r.TargetName)).Order());
    }
}
