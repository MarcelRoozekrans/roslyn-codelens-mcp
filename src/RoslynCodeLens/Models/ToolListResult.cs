namespace RoslynCodeLens.Models;

public record ToolListResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    bool Truncated,
    int Limit,
    object? Summary = null);

public static class ToolListResult
{
    public static ToolListResult<T> Create<T>(
        IReadOnlyList<T> items,
        int limit,
        object? summary = null)
    {
        var truncated = items.Count > limit;
        var sliced = truncated
            ? (IReadOnlyList<T>)items.Take(limit).ToList()
            : items;
        return new ToolListResult<T>(sliced, items.Count, truncated, limit, summary);
    }
}
