using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class GetPublicApiSurfaceTool
{
    [McpServerTool(Name = "get_public_api_surface")]
    [Description(
        "Enumerate every public and protected type and member declared in production " +
        "projects of the active solution. Returns a deterministically-sorted (name ASC) " +
        "flat list of API entries (kind, fully-qualified name, accessibility, project, " +
        "file, line) plus per-kind/per-project/per-accessibility summary buckets. " +
        "Skips test projects, generated code, compiler-generated members, internal " +
        "symbols, and protected members on sealed types (unreachable). Inherited " +
        "members are not repeated under derived types — only declared members appear.")]
    public static GetPublicApiSurfaceResult Execute(MultiSolutionManager manager)
    {
        manager.EnsureLoaded();
        var context = manager.GetAnalysisContext();
        return GetPublicApiSurfaceLogic.Execute(
            context.Loaded,
            context.Resolver);
    }
}
