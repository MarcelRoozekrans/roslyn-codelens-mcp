namespace RoslynCodeLens.Models;

public record ControlFlowInfo(
    bool StartPointIsReachable,
    bool EndPointIsReachable,
    IReadOnlyList<string> ReturnStatements,
    IReadOnlyList<string> ExitPoints,
    bool Succeeded);
