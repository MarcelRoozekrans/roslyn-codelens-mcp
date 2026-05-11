using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class ListTrustedPathsTool
{
    [McpServerTool(Name = "list_trusted_paths"),
     Description("Return the current trust state: session-scoped paths, persistent paths, trusted roots, and analyzer policy.")]
    public static TrustSnapshot Execute(TrustStore trustStore) => trustStore.GetSnapshot();
}
