namespace TestLib;

public class Validator
{
    public void Validate(string input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (input.Length == 0) throw new ArgumentException("Empty");
    }

    // Deliberately nested. Cyclomatic scores this 4, cognitive 6 — the only member in the
    // fixture where the two metrics DISAGREE, which is what makes the `metric` parameter
    // testable at all. Do not flatten it.
    public int SumPositives(int[] values)
    {
        var total = 0;
        foreach (var value in values)       // cyclomatic +1; cognitive +1 (nesting 0)
            if (value > 0)                  // cyclomatic +1; cognitive +2 (nesting 1)
                while (total < value)       // cyclomatic +1; cognitive +3 (nesting 2)
                    total++;
        return total;
    }
}
