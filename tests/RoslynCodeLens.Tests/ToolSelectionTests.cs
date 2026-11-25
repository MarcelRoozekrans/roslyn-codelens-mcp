using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace RoslynCodeLens.Tests;

public class ToolSelectionTests
{
    [Fact]
    public void KnownToolNames_MatchTheToolsTheSdkRegisters()
    {
        // CliOptions validates --tools against KnownToolNames, while the filter compares the
        // names of the registered tools. Both must agree, or an accepted name could match no tool.
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolsFromAssembly(typeof(ToolSelection).Assembly);
        using var provider = services.BuildServiceProvider();
        var registered = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
            .Select(t => t.ProtocolTool.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(registered);
        Assert.Equal(registered, ToolSelection.KnownToolNames);
    }

    [Fact]
    public void Filter_NoSelection_ReturnsAllTools()
    {
        var tools = CreateTools("find_callers", "go_to_definition", "rename_symbol");

        var filtered = ToolSelection.Filter(tools, selected: null);

        Assert.Equal(tools, filtered);
    }

    [Fact]
    public void Filter_Selection_ReturnsOnlySelectedTools()
    {
        var tools = CreateTools("find_callers", "go_to_definition", "rename_symbol");

        var filtered = ToolSelection.Filter(tools, new HashSet<string>(StringComparer.Ordinal) { "go_to_definition", "find_callers" });

        Assert.Equal(["find_callers", "go_to_definition"], filtered.Select(t => t.ProtocolTool.Name), StringComparer.Ordinal);
    }

    private static McpServerTool[] CreateTools(params string[] names) =>
        names.Select(n => McpServerTool.Create(() => n, new McpServerToolCreateOptions { Name = n })).ToArray();
}
