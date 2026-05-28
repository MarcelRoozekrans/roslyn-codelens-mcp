namespace RoslynCodeLens.Models;

public sealed record BackgroundTaskInfo(
    string TaskId,
    string ToolName,
    BackgroundTaskStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    object? Result,
    string? ErrorMessage,
    string? ErrorCode);
