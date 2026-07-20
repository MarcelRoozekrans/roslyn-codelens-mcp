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
                 "(1) `allowOnly` evaluates ONLY solution-internal targets: references to framework " +
                 "and NuGet namespaces such as `System.Collections.Generic` are ignored, otherwise " +
                 "every file would violate every `allowOnly` rule. To restrict a framework " +
                 "namespace, write an explicit `forbid` — that path DOES evaluate metadata targets. " +
                 "(2) Self-references are always allowed: a scope depending on itself is never a " +
                 "violation, under either kind. " +
                 "Results are grouped per violated `rule` plus `sourceScope` plus `targetScope` " +
                 "edge, each with a full `referenceCount` and the first `maxSitesPerViolation` " +
                 "sites. Sorted by rule order, then by descending reference count. Generated code " +
                 "is skipped. Envelope adds a `byRule` / `totalReferences` / `rulesEvaluated` " +
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

        return BuildResult(raw, parsed.Count, limit ?? DefaultLimit);
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
        IReadOnlyList<ArchitectureViolation> raw, int rulesEvaluated, int limit)
        => ToolListResult.Create(raw, limit, BuildSummary(raw, rulesEvaluated));

    internal static object BuildSummary(IReadOnlyList<ArchitectureViolation> items, int rulesEvaluated)
    {
        // Keyed by kind + from + to, not by `from` alone: two rules can share a source pattern
        // (a `forbid` and an `allowOnly` over the same layer is a normal thing to write), and
        // collapsing them into one bucket would silently merge their counts.
        var byRule = items
            .GroupBy(v => $"{v.RuleKind} {v.FromPattern} -> {v.ToPattern}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(v => v.ReferenceCount), StringComparer.Ordinal);

        return new
        {
            byRule,
            totalReferences = items.Sum(v => v.ReferenceCount),
            rulesEvaluated,
        };
    }
}
