namespace RoslynCodeLens.Models;

public record MethodAnalysis(
    string Symbol,
    string? File,
    int Line,
    string? Project,
    string Signature,
    IReadOnlyList<CallerInfo> Callers,
    IReadOnlyList<string> OutgoingCalls);
