namespace RoslynCodeLens.Analysis;

/// <summary>
/// Matcher for architecture-rule scope patterns (namespace or project names).
/// Three forms only, so a pattern's meaning is obvious from reading it:
///   * <c>*</c>              — matches every scope, including the global namespace ("").
///   * <c>Demo.Domain.*</c>  — that scope AND everything beneath it, on segment boundaries.
///   * <c>Demo.Domain</c>    — exactly that scope; children do NOT match.
/// Comparison is ordinal: C# namespaces and project names are case-sensitive.
/// </summary>
public static class ScopePattern
{
    private const string MatchAll = "*";
    private const string WildcardSuffix = ".*";

    /// <summary>
    /// True when the pattern is one of the three supported forms. Interior or suffix
    /// wildcards (<c>Demo.*.Orders</c>, <c>*.Orders</c>, <c>Demo.Domain*</c>) are rejected
    /// rather than silently reinterpreted — a rule that does not mean what it reads is worse
    /// than no rule.
    /// </summary>
    public static bool IsValid(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        if (string.Equals(pattern, MatchAll, StringComparison.Ordinal))
            return true;

        var wildcards = pattern.Count(c => c == '*');
        if (wildcards == 0)
            return true;

        // Exactly one '*', and it must terminate the pattern as a ".*" suffix with a
        // non-empty prefix in front of it.
        return wildcards == 1
            && pattern.EndsWith(WildcardSuffix, StringComparison.Ordinal)
            && pattern.Length > WildcardSuffix.Length;
    }

    /// <summary>
    /// True when <paramref name="scope"/> falls inside <paramref name="pattern"/>.
    /// Callers are expected to have validated the pattern; an invalid one matches nothing.
    /// </summary>
    public static bool Matches(string pattern, string? scope)
    {
        if (!IsValid(pattern))
            return false;

        scope ??= string.Empty;

        if (string.Equals(pattern, MatchAll, StringComparison.Ordinal))
            return true;

        if (pattern.EndsWith(WildcardSuffix, StringComparison.Ordinal))
        {
            var prefix = pattern[..^WildcardSuffix.Length];
            return string.Equals(scope, prefix, StringComparison.Ordinal)
                || (scope.Length > prefix.Length
                    && scope.StartsWith(prefix, StringComparison.Ordinal)
                    && scope[prefix.Length] == '.');
        }

        return string.Equals(scope, pattern, StringComparison.Ordinal);
    }
}
