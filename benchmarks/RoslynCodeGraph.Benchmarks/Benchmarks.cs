using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Build.Locator;
using RoslynCodeGraph;
using RoslynCodeGraph.Tools;

namespace RoslynCodeGraph.Benchmarks;

[MemoryDiagnoser]
public class CodeGraphBenchmarks
{
    private static readonly string FixturePath;
    private LoadedSolution _loaded = null!;
    private SymbolResolver _resolver = null!;

    static CodeGraphBenchmarks()
    {
        MSBuildLocator.RegisterDefaults();

        // Walk up from the base directory to find the repo root (contains RoslynCodeGraph.slnx)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RoslynCodeGraph.slnx")))
            dir = dir.Parent;

        if (dir == null)
            throw new InvalidOperationException(
                "Could not find repo root (RoslynCodeGraph.slnx) starting from " + AppContext.BaseDirectory);

        FixturePath = Path.Combine(dir.FullName,
            "tests", "RoslynCodeGraph.Tests", "Fixtures", "TestSolution", "TestSolution.slnx");
    }

    [GlobalSetup]
    public async Task Setup()
    {
        _loaded = await new SolutionLoader().LoadAsync(FixturePath);
        _resolver = new SymbolResolver(_loaded);
    }

    [Benchmark(Description = "Load and compile solution")]
    public async Task<LoadedSolution> SolutionLoading()
    {
        return await new SolutionLoader().LoadAsync(FixturePath);
    }

    [Benchmark(Description = "find_implementations: IGreeter")]
    public object FindImplementations()
    {
        return FindImplementationsLogic.Execute(_loaded, _resolver, "IGreeter");
    }

    [Benchmark(Description = "find_callers: IGreeter.Greet")]
    public object FindCallers()
    {
        return FindCallersLogic.Execute(_loaded, _resolver, "IGreeter.Greet");
    }

    [Benchmark(Description = "get_type_hierarchy: Greeter")]
    public object GetTypeHierarchy()
    {
        return GetTypeHierarchyLogic.Execute(_loaded, _resolver, "Greeter")!;
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
        return GetSymbolContextLogic.Execute(_loaded, _resolver, "GreeterConsumer")!;
    }

    [Benchmark(Description = "find_reflection_usage: all")]
    public object FindReflectionUsage()
    {
        return FindReflectionUsageLogic.Execute(_loaded, _resolver, null);
    }

    [Benchmark(Description = "find_references: IGreeter")]
    public object FindReferences()
    {
        return FindReferencesLogic.Execute(_loaded, _resolver, "IGreeter");
    }

    [Benchmark(Description = "go_to_definition: Greeter")]
    public object GoToDefinition()
    {
        return GoToDefinitionLogic.Execute(_resolver, "Greeter");
    }

    [Benchmark(Description = "get_diagnostics: all")]
    public object GetDiagnostics()
    {
        return GetDiagnosticsLogic.Execute(_loaded, _resolver, null, null);
    }

    [Benchmark(Description = "search_symbols: Greet")]
    public object SearchSymbols()
    {
        return SearchSymbolsLogic.Execute(_resolver, "Greet");
    }

    [Benchmark(Description = "get_nuget_dependencies: all")]
    public object GetNugetDependencies()
    {
        return GetNugetDependenciesLogic.Execute(_loaded, null)!;
    }

    [Benchmark(Description = "find_attribute_usages: Obsolete")]
    public object FindAttributeUsages()
    {
        return FindAttributeUsagesLogic.Execute(_loaded, _resolver, "Obsolete");
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

    [Benchmark(Description = "find_unused_symbols: all")]
    public object FindUnusedSymbols()
    {
        return FindUnusedSymbolsLogic.Execute(_loaded, _resolver, null, false);
    }
}
