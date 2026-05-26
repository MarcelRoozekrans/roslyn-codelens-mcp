using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class McpToolExceptionFormatterTests
{
    [Fact]
    public void Format_McpToolException_ProducesStructuredJson()
    {
        var ex = new McpToolException(
            ToolErrorCode.SolutionNotTrusted,
            "Solution 'Foo.sln' is not trusted.",
            new { solutionPath = "C:\\Foo.sln" });

        var text = McpToolExceptionFormatter.FormatAsContentText(ex);
        Assert.Contains("\"code\":\"SolutionNotTrusted\"", text, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"Solution 'Foo.sln' is not trusted.\"", text, StringComparison.Ordinal);
        Assert.Contains("\"solutionPath\":\"C:\\\\Foo.sln\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_McpToolException_OmitsDetailsKeyWhenNull()
    {
        var ex = new McpToolException(ToolErrorCode.SymbolNotFound, "X");
        var text = McpToolExceptionFormatter.FormatAsContentText(ex);
        Assert.DoesNotContain("\"details\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OtherException_DefaultsToInternalCode()
    {
        var ex = new InvalidOperationException("boom");
        var text = McpToolExceptionFormatter.FormatAsContentText(ex);
        Assert.Contains("\"code\":\"Internal\"", text, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"boom\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OperationCanceledException_Throws()
    {
        // Cancellation must not be formatted - it should bubble unchanged for the MCP framework to handle.
        var ex = new OperationCanceledException();
        Assert.Throws<InvalidOperationException>(() => McpToolExceptionFormatter.FormatAsContentText(ex));
    }
}
