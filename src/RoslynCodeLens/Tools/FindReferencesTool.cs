using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Analysis;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindReferencesTool
{
    private const int DefaultLimit = 500;

    private static readonly IReadOnlySet<string> ValidKinds =
        new HashSet<string>(ReferenceClassifier.AllKinds, StringComparer.Ordinal);

    [McpServerTool(Name = "find_references"),
     Description("Find all references to a symbol (type, method, property, field, or event) across the " +
                 "solution, each tagged with a kind. Kinds: `read`, `write`, `readwrite` (compound " +
                 "assignment / `++` / `ref`), `invocation`, `method_group`, `object_creation`, `cast`, " +
                 "`type_check` (`is` / patterns / `as`-tests), `typeof`, `base_type`, `type_constraint`, " +
                 "`type_argument`, `declaration`, `attribute`, `nameof`, `xml_doc`. Pass `kinds` to return " +
                 "only some (e.g. `[\"write\",\"readwrite\"]` for mutation sites). Envelope adds a `byKind` " +
                 "summary. Multiple references on one line are reported separately with a `column`.")]
    public static ToolListResult<SymbolReference> Execute(
        MultiSolutionManager manager,
        [Description("Symbol name: simple type (`MyClass`), fully qualified (`Namespace.MyClass`), or member (`MyClass.MyProperty`)")]
            string symbol,
        [Description("Optional kind filter - only references of these kinds are returned (see the kind list above)")]
            string[]? kinds = null,
        [Description("Maximum number of items to return (default: 500). Items are sorted by file, line, column.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        // Validate before the solution-wide scan so a typo fails fast.
        ValidateKinds(kinds);

        var context = manager.GetAnalysisContext();
        var raw = FindReferencesLogic.Execute(context.Loaded, context.Resolver, context.Metadata, symbol);

        return BuildResult(raw, kinds, limit ?? DefaultLimit);
    }

    internal static void ValidateKinds(string[]? kinds)
    {
        if (kinds is not { Length: > 0 })
            return;

        var unknown = kinds.Where(k => !ValidKinds.Contains(k)).ToList();
        if (unknown.Count == 0)
            return;

        throw new McpToolException(
            ToolErrorCode.InvalidArgument,
            $"Unknown reference kind(s): {string.Join(", ", unknown)}.",
            new { validKinds = ValidKinds.OrderBy(k => k, StringComparer.Ordinal).ToArray() });
    }

    /// <summary>
    /// Filters server-side <em>before</em> the limit, so totalCount reports how many references
    /// of the requested kinds exist rather than how many the symbol has overall.
    /// </summary>
    internal static ToolListResult<SymbolReference> BuildResult(
        IReadOnlyList<SymbolReference> raw, string[]? kinds, int limit)
    {
        if (kinds is { Length: > 0 })
        {
            var wanted = new HashSet<string>(kinds, StringComparer.Ordinal);
            raw = raw.Where(r => wanted.Contains(r.ReferenceKind)).ToList();
        }

        return ToolListResult.Create(Sort(raw), limit, BuildSummary(raw));
    }

    internal static IReadOnlyList<SymbolReference> Sort(IReadOnlyList<SymbolReference> items)
        => items
            .OrderBy(r => r.File, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.Column)
            .ToList();

    internal static object BuildSummary(IReadOnlyList<SymbolReference> items)
    {
        var byProject = items
            .GroupBy(r => r.Project, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byKind = items
            .GroupBy(r => r.ReferenceKind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byProject, byKind };
    }
}
