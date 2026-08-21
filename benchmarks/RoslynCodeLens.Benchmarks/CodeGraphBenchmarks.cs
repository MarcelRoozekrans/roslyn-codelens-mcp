using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Build.Locator;
using RoslynCodeLens;
using RoslynCodeLens.Symbols;
using RoslynCodeLens.Tools;
using RoslynCodeLens.Metadata;

namespace RoslynCodeLens.Benchmarks;

[MemoryDiagnoser]
public class CodeGraphBenchmarks
{
    private static readonly string FixturePath;
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;
    private MetadataSymbolResolver _metadata = null!;
    private string _greeterPath = null!;
    private string _diSetupPath = null!;
    private PEFileCache _peFileCache = null!;
    private IlDisassemblerAdapter _ilDisassembler = null!;
    private string _breakingChangesBaselinePath = null!;

    static CodeGraphBenchmarks()
    {
        MSBuildLocator.RegisterDefaults();
        FixturePath = FindFixturePath();
    }

    private static string FindFixturePath()
    {
        // Walk up from the base directory to find the repo root (contains RoslynCodeLens.slnx)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RoslynCodeLens.slnx")))
            dir = dir.Parent;

        return dir == null
            ? throw new InvalidOperationException(
                "Could not find repo root (RoslynCodeLens.slnx) starting from " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName,
                "tests", "RoslynCodeLens.Tests", "Fixtures", "TestSolution", "TestSolution.slnx");
    }

    [GlobalSetup]
    public async Task Setup()
    {
        _loaded = await new SolutionLoader().LoadAsync(FixturePath).ConfigureAwait(false);
        _resolver = new SymbolResolver(_loaded);
        _metadata = new MetadataSymbolResolver(_loaded, _resolver);
        _greeterPath = _loaded.Solution.Projects
            .First(p => string.Equals(p.Name, "TestLib", StringComparison.Ordinal))
            .Documents.First(d => string.Equals(d.Name, "Greeter.cs", StringComparison.Ordinal))
            .FilePath!;
        _diSetupPath = _loaded.Solution.Projects
            .First(p => string.Equals(p.Name, "TestLib2", StringComparison.Ordinal))
            .Documents.First(d => string.Equals(d.Name, "DiSetup.cs", StringComparison.Ordinal))
            .FilePath!;
        _peFileCache = new PEFileCache();
        _ilDisassembler = new IlDisassemblerAdapter(_peFileCache);

        // Pre-generate a baseline JSON snapshot once for find_breaking_changes benchmarks.
        var surface = GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        _breakingChangesBaselinePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(_breakingChangesBaselinePath, System.Text.Json.JsonSerializer.Serialize(surface, opts));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _peFileCache.Dispose();
        if (File.Exists(_breakingChangesBaselinePath))
            File.Delete(_breakingChangesBaselinePath);
    }

    [Benchmark(Description = "Load and compile solution")]
    public async Task<LoadedSolution> SolutionLoading()
    {
        return await new SolutionLoader().LoadAsync(FixturePath).ConfigureAwait(false);
    }

    [Benchmark(Description = "find_implementations: IGreeter")]
    public object FindImplementations()
    {
        return FindImplementationsLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");
    }

    [Benchmark(Description = "find_callers: IGreeter.Greet")]
    public object FindCallers()
    {
        return FindCallersLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");
    }

    [Benchmark(Description = "find_event_subscribers: Clicked")]
    public object FindEventSubscribers()
    {
        return FindEventSubscribersLogic.Execute(
            _loaded, _resolver, _metadata, "EventPublisher.Clicked");
    }

    [Benchmark(Description = "find_tests_for_symbol: IGreeter.Greet")]
    public object FindTestsForSymbol()
    {
        return FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "IGreeter.Greet", transitive: false, maxDepth: 3);
    }

    [Benchmark(Description = "find_tests_for_symbol (transitive): IGreeter.Greet")]
    public object FindTestsForSymbolTransitive()
    {
        return FindTestsForSymbolLogic.Execute(
            _loaded, _resolver, "IGreeter.Greet", transitive: true, maxDepth: 3);
    }

    [Benchmark(Description = "get_test_summary: whole solution")]
    public object GetTestSummary()
    {
        return GetTestSummaryLogic.Execute(_loaded, _resolver, project: null);
    }

    [Benchmark(Description = "get_type_hierarchy: Greeter")]
    public object GetTypeHierarchy()
    {
        return GetTypeHierarchyLogic.Execute(_resolver, _metadata, "Greeter")!;
    }

    [Benchmark(Description = "get_di_registrations: IGreeter")]
    public object GetDiRegistrations()
    {
        return GetDiRegistrationsLogic.Execute(_loaded, _resolver, "IGreeter");
    }

    [Benchmark(Description = "get_project_dependencies: TestLib2")]
    public object GetProjectDependencies()
    {
        return GetProjectDependenciesLogic.Execute(_loaded, "TestLib2")!;
    }

    [Benchmark(Description = "get_symbol_context: GreeterConsumer")]
    public object GetSymbolContext()
    {
        return GetSymbolContextLogic.Execute(_loaded, _resolver, _metadata, "GreeterConsumer")!;
    }

    [Benchmark(Description = "find_reflection_usage: all")]
    public object FindReflectionUsage()
    {
        return FindReflectionUsageLogic.Execute(_loaded, _resolver, null);
    }

    [Benchmark(Description = "find_references: IGreeter")]
    public object FindReferences()
    {
        return FindReferencesLogic.Execute(_loaded, _resolver, _metadata, "IGreeter");
    }

    [Benchmark(Description = "go_to_definition: Greeter")]
    public object GoToDefinition()
    {
        return GoToDefinitionLogic.Execute(_resolver, _metadata, "Greeter");
    }

    [Benchmark(Description = "get_diagnostics: all")]
    public object GetDiagnostics()
    {
        return GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
    }

    [Benchmark(Description = "search_symbols: Greet")]
    public object SearchSymbols()
    {
        return SearchSymbolsLogic.Execute(_resolver, _metadata, "Greet");
    }

    [Benchmark(Description = "get_nuget_dependencies: all")]
    public object GetNugetDependencies()
    {
        return GetNugetDependenciesLogic.Execute(_loaded, null)!;
    }

    [Benchmark(Description = "find_attribute_usages: Obsolete")]
    public object FindAttributeUsages()
    {
        return FindAttributeUsagesLogic.Execute(_loaded, _resolver, _metadata, "Obsolete");
    }

    [Benchmark(Description = "find_obsolete_usage: whole solution")]
    public object FindObsoleteUsage()
    {
        return FindObsoleteUsageLogic.Execute(_loaded, _resolver, project: null, errorOnly: false);
    }

    [Benchmark(Description = "inspect_external_assembly: summary mode")]
    public object InspectExternalAssemblySummary()
    {
        return InspectExternalAssemblyLogic.Execute(
            _metadata, "Microsoft.Extensions.DependencyInjection.Abstractions", "summary", null);
    }

    [Benchmark(Description = "inspect_external_assembly: namespace mode")]
    public object InspectExternalAssemblyNamespace()
    {
        return InspectExternalAssemblyLogic.Execute(
            _metadata, "Microsoft.Extensions.DependencyInjection.Abstractions", "namespace",
            "Microsoft.Extensions.DependencyInjection");
    }

    [Benchmark(Description = "peek_il: ServiceCollectionServiceExtensions.AddScoped")]
    public object PeekIl()
    {
        return PeekIlLogic.Execute(
            _loaded, _metadata, _ilDisassembler,
            "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Type)");
    }

    [Benchmark(Description = "find_circular_dependencies: project")]
    public object FindCircularDependencies()
    {
        return FindCircularDependenciesLogic.Execute(_loaded, _resolver, "project");
    }

    [Benchmark(Description = "get_complexity_metrics: all")]
    public object GetComplexityMetrics()
    {
        return GetComplexityMetricsLogic.Execute(_loaded, _resolver, null, 10);
    }

    [Benchmark(Description = "find_naming_violations: all")]
    public object FindNamingViolations()
    {
        return FindNamingViolationsLogic.Execute(_loaded, _resolver, null);
    }

    [Benchmark(Description = "find_large_classes: all")]
    public object FindLargeClasses()
    {
        return FindLargeClassesLogic.Execute(_loaded, _resolver, null, 20, 500);
    }

    [Benchmark(Description = "find_god_objects: whole solution")]
    public object FindGodObjects()
    {
        return FindGodObjectsLogic.Execute(
            _loaded, _resolver,
            project: null,
            minLines: 300, minMembers: 15, minFields: 10,
            minIncomingNamespaces: 5, minOutgoingNamespaces: 5);
    }

    [Benchmark(Description = "find_unused_symbols: all")]
    public object FindUnusedSymbols()
    {
        return FindUnusedSymbolsLogic.Execute(_loaded, _resolver, null, false);
    }

    [Benchmark(Description = "get_project_health: whole solution")]
    public object GetProjectHealth()
    {
        return GetProjectHealthLogic.Execute(_loaded, _resolver, project: null, hotspotsPerDimension: 5);
    }

    [Benchmark(Description = "find_uncovered_symbols: whole solution")]
    public object FindUncoveredSymbols()
    {
        return FindUncoveredSymbolsLogic.Execute(_loaded, _resolver);
    }

    [Benchmark(Description = "generate_test_skeleton: type input")]
    public object GenerateTestSkeleton()
    {
        return GenerateTestSkeletonLogic.Execute(
            _loaded, _resolver, symbol: "TestLib.OrderService", framework: "xunit");
    }

    [Benchmark(Description = "get_public_api_surface: whole solution")]
    public object GetPublicApiSurface()
    {
        return GetPublicApiSurfaceLogic.Execute(_loaded, _resolver);
    }

    [Benchmark(Description = "find_breaking_changes: JSON identity roundtrip")]
    public object FindBreakingChanges()
    {
        return FindBreakingChangesLogic.Execute(_loaded, _resolver, _breakingChangesBaselinePath);
    }

    [Benchmark(Description = "find_async_violations: whole solution")]
    public object FindAsyncViolations()
    {
        return FindAsyncViolationsLogic.Execute(_loaded, _resolver);
    }

    [Benchmark(Description = "find_disposable_misuse: whole solution")]
    public object FindDisposableMisuse()
    {
        return FindDisposableMisuseLogic.Execute(_loaded, _resolver);
    }

    [Benchmark(Description = "get_source_generators: all")]
    public async Task<object> GetSourceGenerators()
    {
        return await GetSourceGeneratorsLogic.ExecuteAsync(_loaded, null).ConfigureAwait(false);
    }

    [Benchmark(Description = "get_generated_code: all")]
    public object GetGeneratedCode()
    {
        return GetGeneratedCodeLogic.Execute(_loaded, _resolver, null, null);
    }

    [Benchmark(Description = "analyze_data_flow: Greeter.cs line 8")]
    public async Task<object?> AnalyzeDataFlow()
    {
        return await AnalyzeDataFlowLogic.ExecuteAsync(_loaded, _greeterPath, 8, 8, default).ConfigureAwait(false);
    }

    [Benchmark(Description = "analyze_control_flow: DiSetup.cs lines 10-11")]
    public async Task<object?> AnalyzeControlFlow()
    {
        return await AnalyzeControlFlowLogic.ExecuteAsync(_loaded, _diSetupPath, 10, 11, default).ConfigureAwait(false);
    }

    [Benchmark(Description = "analyze_change_impact: IGreeter.Greet")]
    public object? AnalyzeChangeImpact()
    {
        return AnalyzeChangeImpactLogic.Execute(_loaded, _resolver, _metadata, "IGreeter.Greet");
    }

    [Benchmark(Description = "get_type_overview: Greeter")]
    public object? GetTypeOverview()
    {
        return GetTypeOverviewLogic.Execute(_loaded, _resolver, _metadata, "Greeter");
    }

    [Benchmark(Description = "analyze_method: Greeter.Greet")]
    public object? AnalyzeMethod()
    {
        return AnalyzeMethodLogic.Execute(_loaded, _resolver, _metadata, "Greeter.Greet");
    }

    [Benchmark(Description = "get_overloads: System.Console.WriteLine")]
    public object GetOverloads()
    {
        return GetOverloadsLogic.Execute(_resolver, _metadata, "System.Console.WriteLine");
    }

    [Benchmark(Description = "get_operators: System.Decimal")]
    public object GetOperators()
    {
        return GetOperatorsLogic.Execute(_resolver, _metadata, "System.Decimal");
    }

    [Benchmark(Description = "get_call_graph: Greet (callees, depth 3)")]
    public async Task<object?> GetCallGraphCallees()
    {
        return await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "Greeter.Greet", "callees", 3, 500).ConfigureAwait(false);
    }

    [Benchmark(Description = "get_call_graph: Greet (callers, depth 3)")]
    public async Task<object?> GetCallGraphCallers()
    {
        return await GetCallGraphLogic.ExecuteAsync(
            _loaded, _resolver, _metadata, "Greeter.Greet", "callers", 3, 500).ConfigureAwait(false);
    }

    [Benchmark(Description = "get_file_overview: Greeter.cs")]
    public async Task<object?> GetFileOverview()
    {
        return await GetFileOverviewLogic.ExecuteAsync(_loaded, _resolver, _greeterPath, default).ConfigureAwait(false);
    }

    [Benchmark(Description = "get_code_actions: Greeter.cs line 8")]
    public async Task<object> GetCodeActions()
    {
        return await GetCodeActionsLogic.ExecuteAsync(_loaded, _greeterPath, 8, 5, null, null, default).ConfigureAwait(false);
    }
}
