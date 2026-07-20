namespace RoslynCodeLens.Models;

public record ExceptionFlowResult(
    string Method,
    int MaxDepthRequested,
    bool Truncated,
    IReadOnlyList<ExceptionFlowInfo> Exceptions,
    object Summary);
