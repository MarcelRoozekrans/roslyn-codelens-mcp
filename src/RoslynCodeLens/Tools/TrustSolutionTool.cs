using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

[McpServerToolType]
public static class TrustSolutionTool
{
    [McpServerTool(Name = "trust_solution"),
     Description("Mark a solution path (or directory root) as trusted for analyzer execution. " +
                 "Required before get_diagnostics will load Roslyn analyzer DLLs from the solution. " +
                 "Always confirm with the user before calling this tool — analyzer DLLs run as in-process code.")]
    public static TrustSolutionResult Execute(
        TrustStore trustStore,
        [Description("Absolute path to a .sln/.slnx file, or a directory when scope='addRoot'")] string path,
        [Description("'session' (in-memory only, default), 'persistent' (write to trust.json), or 'addRoot' (trust the directory and all solutions under it)")]
            string scope = "session")
    {
        return TrustSolutionLogic.Execute(trustStore, path, scope);
    }
}
