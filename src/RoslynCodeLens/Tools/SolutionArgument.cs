namespace RoslynCodeLens.Tools;

/// <summary>The optional per-call <c>solution</c> argument shared by every solution-bound tool (#606).</summary>
internal static class SolutionArgument
{
    public const string Description =
        "Optional loaded solution to run against, matched like set_active_solution. " +
        "Defaults to the active solution. Pass it when several agents share this server.";
}
