using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RoslynCodeLens.Models;

namespace RoslynCodeLens;

internal static class McpToolExceptionFormatter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FormatAsContentText(Exception ex)
    {
        if (ex is OperationCanceledException)
            throw new InvalidOperationException(
                "OperationCanceledException must propagate to the MCP framework, not be formatted.");

        ErrorPayload payload = ex switch
        {
            McpToolException mcp => new(mcp.Code.ToString(), mcp.Message, mcp.Details),
            _ => new(nameof(ToolErrorCode.Internal), ex.Message, null),
        };
        return JsonSerializer.Serialize(payload, s_options);
    }

    private sealed record ErrorPayload(string Code, string Message, object? Details);
}
