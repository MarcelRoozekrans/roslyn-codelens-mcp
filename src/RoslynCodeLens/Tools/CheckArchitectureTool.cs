using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

/// <summary>
/// Wire shape for one rule. Kept separate from <see cref="ArchitectureRule"/> so the generated
/// JSON schema can carry per-property descriptions without the model depending on
/// System.ComponentModel.
/// </summary>
public sealed record ArchitectureRuleInput
{
    [Description("`forbid` or `allowOnly`.")]
    public string Kind { get; init; } = string.Empty;

    [Description("Source scope pattern: exact (`Demo.Domain`), prefix wildcard (`Demo.Domain.*`, " +
                 "matching that scope and everything beneath it), or `*` for everything.")]
    public string From { get; init; } = string.Empty;

    [Description("Target scope patterns, same syntax as `from`. Always an array: for `forbid` give " +
                 "the single forbidden pattern; for `allowOnly` give the full permitted set.")]
    public string[] To { get; init; } = [];

    [Description("Optional note echoed back on every violation this rule produces.")]
    public string? Description { get; init; }
}

[McpServerToolType]
public static class CheckArchitectureTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "check_architecture"),
     Description("Check user-supplied layering rules against the solution's real semantic type " +
                 "graph (resolved symbols, not `using` directives — so a fully qualified reference " +
                 "with no `using` is still caught, and an unused `using` is not reported). " +
                 "Rule kinds: `forbid` (a dependency from `from` to `to` is a violation) and " +
                 "`allowOnly` (a dependency from `from` to anything outside `to` is a violation). " +
                 "TWO SEMANTICS YOU MUST KNOW TO READ AN EMPTY RESULT CORRECTLY. " +
                 "(1) `allowOnly` evaluates ONLY solution-internal, non-generated targets: " +
                 "references to framework and NuGet namespaces such as `System.Collections.Generic` " +
                 "are ignored (otherwise every file would violate every `allowOnly` rule), and so " +
                 "are types declared entirely in generated code, which the caller cannot remove. " +
                 "To restrict a framework or generated namespace, write an explicit `forbid` — that " +
                 "path DOES evaluate metadata and generated targets. " +
                 "(2) Self-references are always allowed: a scope depending on itself is never a " +
                 "violation, under either kind. " +
                 "Results are grouped per violated `rule` plus `sourceScope` plus `targetScope` " +
                 "edge, each with a full `referenceCount` and the first `maxSitesPerViolation` " +
                 "sites. Sorted by rule order, then by descending reference count. Generated code " +
                 "is never reported as the SOURCE of a violation under either kind. Envelope adds " +
                 "a `byRule` / `totalReferences` / `rulesEvaluated` " +
                 "summary.")]
    public static ToolListResult<ArchitectureViolation> Execute(
        MultiSolutionManager manager,
        [Description("Layering rules to evaluate, in priority order. Each is `kind` (`forbid` or " +
                     "`allowOnly`), `from`, `to` (array of patterns), and an optional `description`.")]
            ArchitectureRuleInput[] rules,
        [Description("Scope compared by the rules: `namespace` (default) or `project`.")]
            string scope = "namespace",
        [Description("Maximum example sites recorded per violated edge (default: 5). The full " +
                     "reference count is reported regardless.")]
            int maxSitesPerViolation = 5,
        [Description("Maximum number of items to return (default: 100). Items are sorted by rule " +
                     "order, then by descending reference count.")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();

        var parsed = ToModel(rules);
        var raw = CheckArchitectureLogic.Execute(
            context.Loaded, context.Resolver, parsed, scope, maxSitesPerViolation);

        return BuildResult(raw, parsed, limit ?? DefaultLimit);
    }

    internal static IReadOnlyList<ArchitectureRule> ToModel(ArchitectureRuleInput[]? rules)
        => rules is null
            ? []
            : rules
                .Select(r => new ArchitectureRule(r.Kind, r.From, r.To ?? [], r.Description))
                .ToList();

    /// <summary>
    /// Summary is computed over the unsliced list, so `totalReferences` describes the solution
    /// rather than the page the caller happened to receive.
    /// </summary>
    internal static ToolListResult<ArchitectureViolation> BuildResult(
        IReadOnlyList<ArchitectureViolation> raw, IReadOnlyList<ArchitectureRule> rules, int limit)
        => ToolListResult.Create(raw, limit, BuildSummary(raw, rules));

    internal static object BuildSummary(
        IReadOnlyList<ArchitectureViolation> items, IReadOnlyList<ArchitectureRule> rules)
    {
        // Keyed by the RULE AS WRITTEN, so one rule is always one bucket. Keying on the matched
        // `to` pattern instead split a `forbid` with several `to` patterns across several
        // buckets while `rulesEvaluated` reported one — a summary that contradicted itself.
        // The index is part of the key because two rules can be textually identical, and the
        // kind/from/to text is there because an index alone tells the reader nothing.
        var byRule = items
            .GroupBy(v => RuleKey(v.RuleIndex, rules[v.RuleIndex]), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(v => v.ReferenceCount), StringComparer.Ordinal);

        return new
        {
            byRule,
            totalReferences = items.Sum(v => v.ReferenceCount),
            rulesEvaluated = rules.Count,
        };
    }

    private static string RuleKey(int index, ArchitectureRule rule)
        => $"[{index}] {rule.Kind} {rule.From} -> {string.Join(", ", rule.To)}";
}
