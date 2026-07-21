using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetExtensionMethodsTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "get_extension_methods")]
    [Description(
        "Answer 'what extension members can I call on this type?' — every extension method and " +
        "C# 14 extension property applicable to a receiver type, from solution source AND " +
        "referenced metadata (so BCL LINQ such as `Where`, `Select`, `Chunk` is included). " +
        "Applicability is decided by the compiler's own reduction, not by name matching: " +
        "`this IEnumerable<T>` is reported for `string` (which is `IEnumerable<char>`) while " +
        "`this IEnumerable<string>` is not. Each entry carries the reduced call-site signature " +
        "(receiver dropped, generic inference applied, return type first: " +
        "`IEnumerable<int> Where<int>(Func<int, bool>)`, `int Tripled`), kind (`method` or " +
        "`property`), declaring type, namespace, origin (`source` or `metadata`), source file/line " +
        "for source members, XML doc summary, and `isStatic`. Read `isStatic` before writing the " +
        "call: false means an instance call (`value.Doubled()`) and is the normal case even though " +
        "every extension method is DECLARED static; true means a C# 14 static extension member, " +
        "called on the type itself (`int.Zero`). " +
        "Results are NOT filtered by `using` scope — the tool has no call-site position, so every " +
        "applicable member is reported and its namespace is given for you to add the import. " +
        "Pass a type name: simple (`Widget`), fully qualified (`MyApp.Widget`), a C# keyword " +
        "(`int`, `string`), a constructed generic (`List<int>`), an array (`string[]`), a nullable " +
        "(`int?`), or a tuple (`(int, string)`). " +
        "Sort: source before metadata, then declaring type, then name.")]
    public static ToolListResult<ExtensionMemberInfo> Execute(
        MultiSolutionManager manager,
        [Description(
            "Receiver type — e.g. `Widget`, `MyApp.Widget`, `int`, `List<int>`, `string[]`, " +
            "`int?`, `(int, string)`")]
            string type,
        [Description("Optional case-insensitive substring filter on the member name — e.g. `chunk`")]
            string? nameFilter = null,
        [Description("Maximum number of items to return (default: 100)")]
            int? limit = null)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        var items = GetExtensionMethodsLogic.Execute(
            context.Loaded, context.Resolver, context.Metadata, type, nameFilter);

        return ToolListResult.Create(items, limit ?? DefaultLimit, BuildSummary(items));
    }

    internal static object BuildSummary(IReadOnlyList<ExtensionMemberInfo> items)
    {
        var byOrigin = items
            .GroupBy(i => i.Origin, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byDeclaringType = items
            .GroupBy(i => i.DeclaringType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new { byOrigin, byDeclaringType };
    }
}
