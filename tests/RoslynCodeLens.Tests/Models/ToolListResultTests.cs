using RoslynCodeLens.Models;

namespace RoslynCodeLens.Tests.Models;

public class ToolListResultTests
{
    [Fact]
    public void Create_EmptyList_ReturnsZeroCountNotTruncated()
    {
        var result = ToolListResult.Create<int>(Array.Empty<int>(), limit: 10);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.False(result.Truncated);
        Assert.Equal(10, result.Limit);
        Assert.Null(result.Summary);
    }

    [Fact]
    public void Create_BelowLimit_NotTruncated()
    {
        var result = ToolListResult.Create(new[] { 1, 2, 3 }, limit: 10);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(3, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Create_ExactlyAtLimit_NotTruncated()
    {
        var result = ToolListResult.Create(new[] { 1, 2, 3 }, limit: 3);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal(3, result.TotalCount);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Create_AboveLimit_Truncated()
    {
        var result = ToolListResult.Create(new[] { 1, 2, 3, 4, 5 }, limit: 2);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new[] { 1, 2 }, result.Items);
        Assert.Equal(5, result.TotalCount);
        Assert.True(result.Truncated);
        Assert.Equal(2, result.Limit);
    }

    [Fact]
    public void Create_PreservesSummary()
    {
        var summary = new { error = 3, warning = 7 };
        var result = ToolListResult.Create(new[] { 1, 2 }, limit: 10, summary);
        Assert.Same(summary, result.Summary);
    }
}
