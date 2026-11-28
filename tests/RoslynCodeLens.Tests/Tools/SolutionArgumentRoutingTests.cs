using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using RoslynCodeLens;
using RoslynCodeLens.BackgroundTasks;
using RoslynCodeLens.Models;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tests.Tools;

/// <summary>
/// Calls every solution-bound tool through its MCP entry point and checks that it honours the
/// per-call <c>solution</c> argument (#606): a name that is not loaded fails, and a loaded name is
/// used even when no solution is active. Arguments are placeholders; what is checked is which
/// solution the call reaches, not what the tool returns.
/// </summary>
public class SolutionArgumentRoutingTests
{
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        "set_active_solution", "unload_solution", "load_solution", "list_solutions",
    };

    // Placeholder values a tool would reject before it resolves the solution.
    private static readonly Dictionary<(string Tool, string Parameter), object?> Overrides = new()
    {
        [("start_background_task", "toolName")] = "rebuild_solution",
    };

    // Never written: no test here trusts a solution, so the file is never created.
    private static readonly TrustStore Trust =
        new(Path.Combine(Path.GetTempPath(), $"codelens-606-trust-{Guid.NewGuid():N}.json"));

    private static readonly string LoadedKey = Path.GetFullPath("/tmp/606/Loaded.sln");

    public static TheoryData<string> SolutionBoundTools()
    {
        var data = new TheoryData<string>();
        foreach (var (_, name) in Tools())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SolutionBoundTools))]
    public async Task UnknownSolution_FailsWithProjectNotFound(string tool)
    {
        using var multi = MultiSolutionManager.CreateForTesting(
            new Dictionary<string, SolutionManager>(StringComparer.Ordinal) { [LoadedKey] = SolutionManager.CreateEmpty() },
            LoadedKey);

        var ex = await Invoke(tool, multi, solution: "NotLoaded");

        var toolEx = Assert.IsType<McpToolException>(ex);
        Assert.Equal(ToolErrorCode.ProjectNotFound, toolEx.Code);
    }

    [Theory]
    [MemberData(nameof(SolutionBoundTools))]
    public async Task NamedSolution_IsUsedWhenNoneIsActive(string tool)
    {
        using var multi = MultiSolutionManager.CreateForTesting(
            new Dictionary<string, SolutionManager>(StringComparer.Ordinal) { [LoadedKey] = SolutionManager.CreateFromLoaded(OneProject()) },
            activeKey: null);

        var ex = await Invoke(tool, multi, solution: "Loaded.sln");

        // Without the argument this would fail with "No solution loaded". Anything else, including a
        // tool error about the placeholder arguments, means the call reached the named solution.
        if (ex is McpToolException toolEx)
            Assert.DoesNotContain("No solution loaded", toolEx.Message, StringComparison.Ordinal);
    }

    // A compiled one-project solution in memory: loaded, so EnsureLoaded passes and each tool runs
    // past it, but with no solution file, watcher or warm-up. Its documents have no path on disk.
    private static LoadedSolution OneProject()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Placeholder", LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        project = project.AddDocument("Placeholder.cs", "public class Placeholder { public void Run() { } }").Project;
        var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()!;
        return new LoadedSolution
        {
            Solution = project.Solution,
            Compilations = new ConcurrentDictionary<ProjectId, Compilation> { [project.Id] = compilation },
        };
    }

    private static IEnumerable<(MethodInfo Method, string Name)> Tools() =>
        typeof(MultiSolutionManager).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attr is not null
                        && x.Method.GetParameters().Any(p => p.ParameterType == typeof(MultiSolutionManager))
                        && !Exempt.Contains(x.Attr.Name!))
            .Select(x => (x.Method, x.Attr!.Name!))
            .OrderBy(x => x.Item2, StringComparer.Ordinal);

    private static async Task<Exception?> Invoke(string tool, MultiSolutionManager multi, string solution)
    {
        var method = Tools().Single(t => string.Equals(t.Name, tool, StringComparison.Ordinal)).Method;
        using var store = new BackgroundTaskStore();
        var args = method.GetParameters().Select(p => Argument(tool, p, multi, store, solution)).ToArray();

        var ex = await Record.ExceptionAsync(async () =>
        {
            switch (method.Invoke(null, args))
            {
                case Task task:
                    await task.ConfigureAwait(false);
                    break;
                case ValueTask valueTask:
                    await valueTask.ConfigureAwait(false);
                    break;
            }
        }).ConfigureAwait(false);

        // A tool that throws before its first await surfaces through reflection.
        return ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
    }

    private static object? Argument(
        string tool, ParameterInfo p, MultiSolutionManager multi, BackgroundTaskStore store, string solution)
    {
        if (p.ParameterType == typeof(MultiSolutionManager)) return multi;
        if (p.ParameterType == typeof(BackgroundTaskStore)) return store;
        if (p.ParameterType == typeof(TrustStore)) return Trust;
        if (p.ParameterType == typeof(AnalyzerAllowlist))
            return new AnalyzerAllowlist(Trust.AnalyzerPolicy, AnalyzerAllowlist.DefaultNugetGlobal(), dotnetSdkRoot: null);
        if (string.Equals(p.Name, "solution", StringComparison.Ordinal)) return solution;
        if (Overrides.TryGetValue((tool, p.Name!), out var value)) return value;
        if (p.ParameterType == typeof(CancellationToken)) return CancellationToken.None;
        if (p.HasDefaultValue) return p.DefaultValue;
        if (p.ParameterType == typeof(string)) return "Placeholder";
        if (p.ParameterType == typeof(int)) return 1;
        if (p.ParameterType.IsArray) return Array.CreateInstance(p.ParameterType.GetElementType()!, 0);
        throw new InvalidOperationException($"No placeholder for {tool}.{p.Name} of type {p.ParameterType}.");
    }
}
