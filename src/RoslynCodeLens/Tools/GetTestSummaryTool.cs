using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetTestSummaryTool
{
    [McpServerTool(Name = "get_test_summary")]
    [Description(
        "Per-project inventory of test methods. Each test reports framework " +
        "(xUnit/NUnit/MSTest), attribute kind ([Fact]/[Theory]/[Test]/[TestCase]/" +
        "[TestMethod]/[DataTestMethod]), data-driven row count, location, and the " +
        "production symbols it references. " +
        "Complements find_tests_for_symbol (which goes test → production); this goes " +
        "project → tests. Use to answer 'what does this test suite cover?' or to break " +
        "down test counts by framework/attribute. " +
        "Production projects, generated code, and BCL/framework calls are filtered out " +
        "of the per-test referenced-symbols list. Project filter is case-insensitive. " +
        "Sort: tests by (file, line); projects by name ASC.")]
    public static GetTestSummaryResult Execute(
        MultiSolutionManager manager,
        [Description("Optional: restrict to a single test project by name (case-insensitive).")]
        string? project = null)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GetTestSummaryLogic.Execute(
            context.Loaded,
            context.Resolver,
            project);
    }
}
