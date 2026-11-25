using System.Reflection;
using ModelContextProtocol.Server;

namespace RoslynCodeLens;

/// <summary>
/// Restricts the registered tools to the names passed with <c>--tools</c>.
/// </summary>
internal static class ToolSelection
{
    /// <summary>
    /// Names of all tools in this assembly, read from <see cref="McpServerToolAttribute.Name"/>.
    /// Read by reflection so <see cref="CliOptions.Parse"/> can reject an unknown name before
    /// the solution is loaded; a test keeps this list equal to the tools the SDK registers.
    /// </summary>
    public static IReadOnlySet<string> KnownToolNames { get; } = typeof(ToolSelection).Assembly
        .GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Returns the tools whose name is in <paramref name="selected"/>, or all tools when
    /// <paramref name="selected"/> is <see langword="null"/>.
    /// </summary>
    public static IEnumerable<McpServerTool> Filter(IEnumerable<McpServerTool> tools, IReadOnlySet<string>? selected) =>
        selected is null ? tools : tools.Where(t => selected.Contains(t.ProtocolTool.Name));
}
