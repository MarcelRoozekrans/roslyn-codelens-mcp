using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace RoslynCodeLens.Tests;

/// <summary>
/// Authoring-time gate for DocGen's MDX pipeline: every tool description and parameter
/// description must keep backticks balanced and put every '&lt;' / '{' inside a backtick
/// code span. DocGen escapes bare ones defensively, but a stray backtick used to disable
/// escaping for the rest of the string — keep the source descriptions clean instead.
/// </summary>
public class ToolDescriptionMdxSafetyTests
{
    private static IEnumerable<(string Label, string Description)> AllDescriptions()
    {
        var assembly = typeof(RoslynCodeLens.Tools.ResolveStackTraceTool).Assembly;
        foreach (var type in assembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

            foreach (var method in methods)
            {
                var toolName = method.GetCustomAttribute<McpServerToolAttribute>()!.Name ?? method.Name;
                var toolDesc = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (toolDesc is not null)
                    yield return (toolName, toolDesc);

                foreach (var parameter in method.GetParameters())
                {
                    var paramDesc = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
                    if (paramDesc is not null)
                        yield return ($"{toolName}({parameter.Name})", paramDesc);
                }
            }
        }
    }

    [Fact]
    public void ReflectionReachesTheToolSurface()
    {
        // Guard against the gate silently going inert (e.g. attribute type mismatch).
        var toolCount = AllDescriptions()
            .Select(d => d.Label.Split('(')[0])
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.True(toolCount >= 59, $"Expected at least 59 tools with descriptions, found {toolCount}.");
    }

    [Fact]
    public void AllToolAndParameterDescriptions_AreMdxSafe()
    {
        var violations = new List<string>();
        foreach (var (label, description) in AllDescriptions())
        {
            if (description.Count(c => c == '`') % 2 != 0)
            {
                violations.Add($"{label}: unbalanced backticks");
                continue; // code-span tracking below would be meaningless
            }

            var inCode = false;
            foreach (var ch in description)
            {
                if (ch == '`') { inCode = !inCode; continue; }
                if (!inCode && ch is '<' or '{')
                {
                    violations.Add($"{label}: bare '{ch}' outside a backtick code span");
                    break;
                }
            }
        }

        Assert.True(violations.Count == 0,
            "MDX-unsafe tool descriptions (wrap mangled-name tokens in backticks):\n  "
            + string.Join("\n  ", violations));
    }
}
