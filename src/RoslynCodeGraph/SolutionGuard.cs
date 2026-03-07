namespace RoslynCodeGraph;

public static class SolutionGuard
{
    public static void EnsureLoaded(LoadedSolution loaded)
    {
        if (loaded.IsEmpty)
            throw new InvalidOperationException(
                "No .sln file found. Either run from a directory containing a .sln/.slnx file, " +
                "or pass the solution path as argument: roslyn-codegraph-mcp /path/to/Solution.sln");
    }
}
