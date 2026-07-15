using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetOperatorsTool
{
    [McpServerTool(Name = "get_operators")]
    [Description(
        "Return every user-defined operator and conversion operator on a type in one call — source AND metadata. " +
        "Each entry includes the operator kind (such as +, ==, implicit, explicit, plus all comparison and bitwise operators), full signature, " +
        "parameter names/types/modifiers, return type, accessibility, an IsCheckedVariant flag for .NET 7+ " +
        "checked operators (op_CheckedAddition etc.), XML doc summary, and source location (empty for metadata). " +
        "Includes compiler-synthesized record equality operators. " +
        "Returns declared operators only — operators do not inherit in C#. " +
        "Pass a type name, simple or fully qualified (e.g. 'Vector2', 'MyApp.Money', 'System.Decimal'). " +
        "Sort: kind ordinal ASC, then parameter count ASC, then signature ordinal ASC.")]
    public static GetOperatorsResult Execute(
        MultiSolutionManager manager,
        [Description("Type name (simple or fully qualified) — e.g. Vector2, MyApp.Money")] string symbol)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GetOperatorsLogic.Execute(context.Resolver, context.Metadata, symbol);
    }
}
