namespace Shared;

public class Publisher
{
    public event EventHandler? Changed;

    public int Compute(int value) => value * 2;

    [Obsolete("Use Compute instead.")]
    public int LegacyCompute(int value) => value;

    // Same name and parameter type as the obsolete overload, differs only by ref: must stay apart.
    public int LegacyCompute(ref int value) => value;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

// Separate type so that TestsMulti does not add to Publisher's incoming namespaces.
public static class Calculator
{
    public static int Halve(int value) => value / 2;
}

[AttributeUsage(AttributeTargets.All)]
public sealed class MarkerAttribute : Attribute;

public interface INotifier
{
    event EventHandler? Notified;
}

// Implemented by the same-named Duplicate.Helper types of AppNet9 and AppNet10.
public interface IRunner
{
}
