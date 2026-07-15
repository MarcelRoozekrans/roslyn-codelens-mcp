using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetOverloadsTool
{
    [McpServerTool(Name = "get_overloads")]
    [Description(
        "Return every overload of a method (or constructor) in one call — source AND metadata. " +
        "Each overload includes the full signature, parameter names/types/modifiers (ref/out/in/" +
        "params/optional with defaults), return type, accessibility, modifiers (static/virtual/" +
        "abstract/override/async/extension), generic type parameters, XML doc summary, and source " +
        "location (empty for metadata). " +
        "Pass 'Type.Method' for ordinary methods or 'Type.Type' for constructors. Operator " +
        "overloads are excluded — use get_operators for those. " +
        "Sort: parameter count ASC, then signature ordinal ASC.")]
    public static GetOverloadsResult Execute(
        MultiSolutionManager manager,
        [Description("Method or constructor symbol (e.g. 'Greeter.Greet', 'Greeter.Greeter', 'System.Console.WriteLine').")]
        string symbol)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GetOverloadsLogic.Execute(
            context.Resolver,
            context.Metadata,
            symbol);
    }
}
