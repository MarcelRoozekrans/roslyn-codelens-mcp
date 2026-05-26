using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynCodeLens;

internal sealed class StructuredErrorToolWrapper(McpServerTool inner) : DelegatingMcpServerTool(inner)
{
    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Let MCP framework handle cancellation natively.
            throw;
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = McpToolExceptionFormatter.FormatAsContentText(ex) }],
            };
        }
    }
}
