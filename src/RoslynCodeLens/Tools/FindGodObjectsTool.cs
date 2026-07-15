using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class FindGodObjectsTool
{
    [McpServerTool(Name = "find_god_objects")]
    [Description(
        "Find types that combine high size with high coupling — 'god classes' that violate " +
        "single-responsibility and become refactoring nightmares. Sharper signal than " +
        "find_large_classes alone: a 1000-line internal helper used only by its own " +
        "namespace is not flagged, but a 200-line class called from 15 different " +
        "namespaces is. " +
        "Two axes: size (lines/members/fields) and coupling (incoming/outgoing namespace " +
        "counts). A type qualifies when it crosses ALL THREE size thresholds AND at least " +
        "one coupling threshold — keeps DTOs (high field count only) and dispatchers " +
        "(high member count only) off the list. " +
        "Defaults: lines >= 300, members >= 15, fields >= 10, incoming-namespaces >= 5, " +
        "outgoing-namespaces >= 5. Each threshold is independently configurable. " +
        "BCL namespaces (System.*, Microsoft.*) excluded from outgoing count. Test " +
        "projects, generated code, interfaces, and nested types are skipped. " +
        "Sort: total axes exceeded DESC, then line count DESC.")]
    public static GodObjectsResult Execute(
        MultiSolutionManager manager,
        [Description("Optional: restrict to a single project by name (case-insensitive).")]
        string? project = null,
        [Description("Min lines for size axis. Default 300.")]
        int minLines = 300,
        [Description("Min member count for size axis. Default 15.")]
        int minMembers = 15,
        [Description("Min field count for size axis. Default 10.")]
        int minFields = 10,
        [Description("Min incoming-namespace count for coupling axis. Default 5.")]
        int minIncomingNamespaces = 5,
        [Description("Min outgoing-namespace count for coupling axis. Default 5.")]
        int minOutgoingNamespaces = 5)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return FindGodObjectsLogic.Execute(
            context.Loaded,
            context.Resolver,
            project,
            minLines,
            minMembers,
            minFields,
            minIncomingNamespaces,
            minOutgoingNamespaces);
    }
}
