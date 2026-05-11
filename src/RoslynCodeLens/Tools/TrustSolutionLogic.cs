using RoslynCodeLens.Security;

namespace RoslynCodeLens.Tools;

public static class TrustSolutionLogic
{
    public static TrustSolutionResult Execute(TrustStore store, string path, string scope)
    {
        switch (scope)
        {
            case "session":
                store.AddSessionTrust(path);
                break;
            case "persistent":
                store.AddPersistentTrust(path);
                break;
            case "addRoot":
                store.AddTrustedRoot(path);
                break;
            default:
                throw new ArgumentException(
                    $"Unknown scope '{scope}'. Use 'session', 'persistent', or 'addRoot'.",
                    nameof(scope));
        }
        return new TrustSolutionResult(path, scope, "Solution trusted.");
    }
}

public sealed record TrustSolutionResult(string Path, string Scope, string Message);
