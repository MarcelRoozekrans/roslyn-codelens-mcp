using RoslynCodeLens.Models;

namespace RoslynCodeLens;

/// <summary>
/// Matches a caller-supplied solution name against the loaded solution keys. One rule for
/// <c>set_active_solution</c>, <c>unload_solution</c> and every tool's <c>solution</c> argument.
/// Tiers, first tier with any match wins: exact full path, exact file name, substring.
/// A name that is unique today under substring matching resolves the same way; the earlier
/// tiers only let an exact path or file name win where substring matching is ambiguous.
/// </summary>
internal static class SolutionMatcher
{
    public static string Match(IReadOnlyCollection<string> keys, string name)
    {
        var fullPath = TryGetFullPath(name);
        var matches = fullPath is null
            ? []
            : keys.Where(k => string.Equals(k, fullPath, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
            matches = keys.Where(k => string.Equals(Path.GetFileName(k), name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
            matches = keys.Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
            throw new McpToolException(
                ToolErrorCode.ProjectNotFound,
                $"No solution matching '{name}'. Available: {string.Join(", ", keys.Select(Path.GetFileName))}",
                new { name, available = keys.Select(Path.GetFileName).ToArray() });

        if (matches.Count > 1)
            throw new McpToolException(
                ToolErrorCode.AmbiguousMatch,
                $"Ambiguous match for '{name}'. Matches: {string.Join(", ", matches)}",
                new { name, matches });

        return matches[0];
    }

    private static string? TryGetFullPath(string name)
    {
        try
        {
            return Path.GetFullPath(name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
