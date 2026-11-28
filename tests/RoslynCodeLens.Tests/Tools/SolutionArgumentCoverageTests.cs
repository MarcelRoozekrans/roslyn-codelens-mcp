using System.Reflection;
using ModelContextProtocol.Server;
using RoslynCodeLens;

namespace RoslynCodeLens.Tests.Tools;

public class SolutionArgumentCoverageTests
{
    // These already take a solution name or path, or describe every solution at once.
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        "set_active_solution", "unload_solution", "load_solution", "list_solutions",
    };

    [Fact]
    public void EverySolutionBoundTool_DeclaresOptionalSolution()
    {
        var tools = typeof(MultiSolutionManager).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attr is not null
                        && x.Method.GetParameters().Any(p => p.ParameterType == typeof(MultiSolutionManager))
                        && !Exempt.Contains(x.Attr.Name!))
            .ToList();

        var missing = tools
            .Where(x => !x.Method.GetParameters().Any(p =>
                string.Equals(p.Name, "solution", StringComparison.Ordinal) && p.ParameterType == typeof(string) && p.HasDefaultValue && p.DefaultValue is null))
            .Select(x => x.Attr!.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, $"Tools without an optional `solution` argument: {string.Join(", ", missing)}");
        Assert.Equal(58, tools.Count);
    }
}
