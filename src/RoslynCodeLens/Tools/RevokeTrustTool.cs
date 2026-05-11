using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class RevokeTrustTool
{
    [McpServerTool(Name = "revoke_trust"),
     Description("Remove a previously trusted solution path or trusted root. Removes both session and persistent entries.")]
    public static string Execute(
        TrustStore trustStore,
        [Description("Absolute path of the solution or trusted root to revoke")] string path)
    {
        trustStore.Revoke(path);
        return $"Trust revoked for: {path}";
    }
}
