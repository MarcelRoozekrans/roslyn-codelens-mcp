using System.Text.RegularExpressions;

namespace RoslynCodeLens;

/// <summary>
/// Pure-logic closure walker. Given a <see cref="ProjectFilter"/>, the full
/// universe of project names, and a <c>name → referenced-names</c> graph,
/// returns the names that should be loaded.
///
/// <para>Seeds = (glob matches of <see cref="ProjectFilter.Include"/>)
/// ∪ (literal matches of <see cref="ProjectFilter.RootProjects"/>).
/// Loaded set = transitive BFS closure over the graph.</para>
/// </summary>
public static class ProjectClosure
{
    public sealed record Result(IReadOnlySet<string> Loaded);

    public static Result Compute(
        ProjectFilter filter,
        IEnumerable<string> allProjectNames,
        IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var allSet = allProjectNames is HashSet<string> hs ? hs : new HashSet<string>(allProjectNames, StringComparer.Ordinal);

        var includeRegexes = new List<Regex>(filter.Include.Count);
        foreach (var pattern in filter.Include)
        {
            try
            {
                includeRegexes.Add(GlobToRegex(pattern));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid include glob '{pattern}': {ex.Message}");
            }
        }

        var seeds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in allSet)
        {
            foreach (var rx in includeRegexes)
            {
                if (rx.IsMatch(name)) { seeds.Add(name); break; }
            }
        }

        var missingRoots = new List<string>();
        foreach (var root in filter.RootProjects)
        {
            if (allSet.Contains(root)) seeds.Add(root);
            else missingRoots.Add(root);
        }

        if (missingRoots.Count > 0)
        {
            throw new InvalidOperationException(
                $"rootProjects names {missingRoots.Count} project(s) that do not exist in the solution: " +
                string.Join(", ", missingRoots) + ".");
        }

        if (seeds.Count == 0)
        {
            var available = string.Join(", ", allSet.OrderBy(n => n, StringComparer.Ordinal).Take(10));
            throw new InvalidOperationException(
                $"Filter matched 0 projects. Available project names (first 10): {available}. " +
                "Call list_solutions after loading without a filter to enumerate all.");
        }

        var loaded = new HashSet<string>(seeds, StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!graph.TryGetValue(current, out var refs)) continue;
            foreach (var next in refs)
            {
                if (!allSet.Contains(next)) continue;
                if (loaded.Add(next)) queue.Enqueue(next);
            }
        }

        return new Result(loaded);
    }

    private static Regex GlobToRegex(string glob)
    {
        // Reject bracket characters explicitly — we don't support character classes,
        // and unescaped `[` would silently become a literal that never matches.
        if (glob.Contains('['))
            throw new ArgumentException("character classes '[…]' are not supported");

        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
