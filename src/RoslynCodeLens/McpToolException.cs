using RoslynCodeLens.Models;

namespace RoslynCodeLens;

public sealed class McpToolException : Exception
{
    public ToolErrorCode Code { get; }
    public object? Details { get; }

    public McpToolException(ToolErrorCode code, string message, object? details = null)
        : base(message)
    {
        Code = code;
        Details = details;
    }
}
