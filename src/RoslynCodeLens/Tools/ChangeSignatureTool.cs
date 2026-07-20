using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ChangeSignatureTool
{
    [McpServerTool(Name = "change_signature"),
     Description("Add, remove, or reorder a method's parameters and update every call site " +
                 "(Roslyn's own change-signature engine). Cascades to overrides and interface " +
                 "implementations, rewrites named and optional arguments, and preserves an " +
                 "extension method's `this` parameter; the overrides and implementations it also " +
                 "rewrote are listed in `CascadedTo`. Operations apply in order: " +
                 "`remove` drops `parameter`; `reorder` takes `order`, a full permutation of the " +
                 "parameters surviving at that point; `add` appends `name` plus `type` and " +
                 "REQUIRES `callSiteValue` — the expression every existing call site will pass, " +
                 "since the tool never guesses call-site semantics — with an optional " +
                 "`defaultValue` that instead makes the parameter optional and leaves existing " +
                 "calls untouched. Rejected as unsafe: an added name that is not a valid C# "
                 + "identifier or collides with an existing parameter; moving or removing an "
                 + "extension method's `this` parameter (it must stay first); and any signature "
                 + "that would leave a surviving `params` array anywhere but last — add the "
                 + "parameter and reorder it before the `params` array, or remove that array. "
                 + "Defaults to preview mode (returns edits without writing files); " +
                 "set `preview=false` to apply. New compiler errors the change would introduce are " +
                 "reported as Conflicts, and apply mode refuses to write them unless `force=true`. " +
                 "Source-defined methods only; overloaded names must be disambiguated.")]
    public static async Task<ChangeSignatureResult> Execute(
        MultiSolutionManager manager,
        [Description("Method to change: `MyClass.MyMethod` or fully qualified `Namespace.MyClass.MyMethod`")] string method,
        [Description("Parameter edits applied in order. Each has `kind` (`remove`, `reorder` or `add`) plus: `parameter` for `remove`; `order` for `reorder`; `name`, `type`, `callSiteValue` and optional `defaultValue` for `add`")] SignatureOperation[] operations,
        [Description("Preview only — return edits without writing to disk (default: true)")] bool preview = true,
        [Description("Apply even when Conflicts are reported (default: false)")] bool force = false,
        CancellationToken ct = default)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return await ChangeSignatureLogic.ExecuteAsync(
            context.Loaded, context.Resolver, method, operations, preview, force,
            // After a successful apply, push the written texts into the in-memory snapshot so
            // follow-up queries see the new signature immediately (no watcher-debounce window).
            manager.CommitDocumentTextsAsync, ct).ConfigureAwait(false);
    }
}
