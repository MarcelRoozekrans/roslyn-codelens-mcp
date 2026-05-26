using RoslynCodeLens;
using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests;

public class McpToolExceptionTests
{
    [Fact]
    public void Ctor_SetsCodeAndMessage()
    {
        var ex = new McpToolException(ToolErrorCode.SymbolNotFound, "X");
        Assert.Equal(ToolErrorCode.SymbolNotFound, ex.Code);
        Assert.Equal("X", ex.Message);
        Assert.Null(ex.Details);
    }

    [Fact]
    public void Ctor_PreservesDetails()
    {
        var details = new { path = "/foo/bar" };
        var ex = new McpToolException(ToolErrorCode.FileNotFound, "missing", details);
        Assert.Same(details, ex.Details);
    }

    [Fact]
    public void Enum_HasAllExpectedCodes()
    {
        // Lock the catalog. Adding a code is intentional; removing one is breaking.
        var expected = new[]
        {
            "SymbolNotFound", "SolutionNotTrusted", "AmbiguousMatch",
            "FileNotFound", "ProjectNotFound", "InvalidArgument", "Internal",
        };
        var actual = Enum.GetNames<ToolErrorCode>();
        Assert.Equal(expected.Length, actual.Length);
        foreach (var name in expected)
            Assert.Contains(name, actual, StringComparer.Ordinal);
    }
}
