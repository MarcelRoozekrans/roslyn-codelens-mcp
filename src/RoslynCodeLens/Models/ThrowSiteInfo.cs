namespace RoslynCodeLens.Models;

public record ThrowSiteInfo(
    string ExceptionType,
    string Method,
    string File,
    int Line,
    int Column,
    string Snippet,
    bool IsRethrow,
    string Project);
