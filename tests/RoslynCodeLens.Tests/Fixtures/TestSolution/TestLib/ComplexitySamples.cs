namespace TestLib;

/// <summary>
/// Deliberately over-complex code, so the default complexity threshold of 10 is actually
/// reachable in this solution.
/// </summary>
/// <remarks>
/// Without a member above 10, every complexity assertion in the suite compared 0 against 0 —
/// <c>GetProjectHealthToolTests.Counts_ComplexityMatchesUnderlyingTool</c> passed while the whole
/// complexity dimension of <c>get_project_health</c> went untested, and would have kept passing if
/// that dimension returned nothing at all.
/// <para>
/// Do not simplify, split, or "clean up" these methods. Their ugliness is the fixture.
/// </para>
/// </remarks>
public class ComplexitySamples
{
    /// <summary>
    /// Cyclomatic 13: 1 + twelve decisions (ten <c>if</c>, two <c>&amp;&amp;</c>).
    /// Comfortably over the default threshold of 10, with room for the number to drift a little
    /// without silently dropping back under it.
    /// </summary>
    public string Classify(int a, int b, string label)
    {
        if (a < 0) return "negative";
        if (a == 0) return "zero";
        if (a > 1000) return "huge";
        if (b < 0) return "b-negative";
        if (b == 0) return "b-zero";
        if (label is null) return "unlabelled";
        if (label.Length == 0) return "empty-label";
        if (a > b && label.Length > 3) return "a-dominant";
        if (b > a && label.Length > 3) return "b-dominant";
        if (a == b) return "equal";
        return "other";
    }

    /// <summary>
    /// Cognitive 15 against cyclomatic 6 — deeply nested rather than merely long, so the two
    /// metrics disagree sharply. Keeps <c>metric: "cognitive"</c> meaningful at the default
    /// threshold, which <c>Validator.SumPositives</c> (cognitive 6) is too small to do.
    /// <para>
    /// The nesting is five levels deep on purpose. At four it scored exactly 10 — sitting on the
    /// threshold, where a one-point drift would drop it out and quietly make the cognitive
    /// assertions vacuous again, which is the very failure this fixture exists to prevent.
    /// </para>
    /// </summary>
    public int DeeplyNested(int[][] grid)
    {
        var total = 0;
        foreach (var row in grid)                   // +1 (nesting 0)
        {
            if (row is not null)                    // +2 (nesting 1)
            {
                foreach (var cell in row)           // +3 (nesting 2)
                {
                    if (cell > 0)                   // +4 (nesting 3)
                    {
                        while (total < cell)        // +5 (nesting 4)
                        {
                            total++;
                        }
                    }
                }
            }
        }

        return total;
    }
}
