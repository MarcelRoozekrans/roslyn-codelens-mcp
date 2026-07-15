using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GenerateTestSkeletonTool
{
    [McpServerTool(Name = "generate_test_skeleton")]
    [Description(
        "Emits a test-class skeleton (parseable C#) for a method or type. " +
        "Pass a type FQN like 'MyApp.Services.OrderService' to get a full test class " +
        "with one stub per public method, or a method FQN like " +
        "'MyApp.Services.OrderService.PlaceOrder' to get a single stub. Returns " +
        "framework, suggested file path, class name, full file contents (as text), " +
        "and TodoNotes for things to wire up (e.g. constructor dependencies). " +
        "The tool does NOT write to disk — agent decides what to do with the text. " +
        "Pairs naturally with find_uncovered_symbols / get_test_summary. " +
        "Stubs include happy-path Fact, Theory + InlineData for primitive-param " +
        "methods, and Assert.Throws assertions per distinct direct-throw exception type. " +
        "Async (Task-returning) methods detected automatically. Properties, indexers, " +
        "operators, and constructors are excluded from per-method enumeration. " +
        "Framework auto-detected from solution test projects (tie → xUnit); " +
        "override with framework='xunit' / 'nunit' / 'mstest'.")]
    public static GenerateTestSkeletonResult Execute(
        MultiSolutionManager manager,
        [Description("FQN of a type or method to generate a test skeleton for.")]
        string symbol,
        [Description("Optional framework override: 'xunit', 'nunit', or 'mstest'. Auto-detected if null.")]
        string? framework = null)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GenerateTestSkeletonLogic.Execute(
            context.Loaded,
            context.Resolver,
            symbol,
            framework);
    }
}
