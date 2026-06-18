using BenchmarkDotNet.Running;
using RoslynCodeLens.Benchmarks;

var switcher = BenchmarkSwitcher.FromTypes(new[]
{
    typeof(CodeGraphBenchmarks),
    typeof(SolutionLoadBenchmarks),
});

// No args → run everything (preserves the prior `dotnet run` behaviour);
// otherwise honour BenchmarkDotNet's CLI filters, e.g. --filter *SolutionLoad*.
switcher.Run(args.Length == 0 ? new[] { "--filter", "*" } : args);
