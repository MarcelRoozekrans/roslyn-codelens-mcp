using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetMethodSourceTool
{
    private const int DefaultLimit = 100;

    [McpServerTool(Name = "get_method_source"),
     Description("Return the full declaration source (XML docs, attributes, signature, body — original " +
                 "formatting) of one or more members by name: methods (all overloads returned), " +
                 "constructors (request as `Widget.Widget` or fully qualified `Ns.Widget.Widget`; " +
                 "nested types need full qualification), properties, indexers (request as `Type.this` " +
                 "or `Type.this[]`), fields, events. " +
                 "Batch-friendly: pass many names in one call instead of reading whole files. " +
                 "Per-item statuses: ok, notFound, ambiguous (with candidates), metadata (use `peek_il` " +
                 "or `inspect_external_assembly`), unsupportedKind (whole types — use `get_type_overview`). " +
                 "Items keep request order.")]
    public static ToolListResult<MemberSourceInfo> Execute(
        MultiSolutionManager manager,
        [Description("Member names: simple (`MyClass.MyMethod`) or fully qualified (`Ns.MyClass.MyMethod`)")] string[] symbols,
        [Description("Maximum number of items to return (default: 100)")] int? limit = null)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        var items = GetMethodSourceLogic.Execute(context.Resolver, context.Metadata, symbols);

        int ok = 0, notFound = 0, ambiguous = 0, metadata = 0, unsupportedKind = 0;
        foreach (var item in items)
        {
            switch (item.Status)
            {
                case "ok": ok++; break;
                case "notFound": notFound++; break;
                case "ambiguous": ambiguous++; break;
                case "metadata": metadata++; break;
                case "unsupportedKind": unsupportedKind++; break;
                default: break;
            }
        }
        var summary = new { byStatus = new { ok, notFound, ambiguous, metadata, unsupportedKind } };
        return ToolListResult.Create(items, limit ?? DefaultLimit, summary);
    }
}
