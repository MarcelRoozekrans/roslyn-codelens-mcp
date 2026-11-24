namespace Shared;

public class Publisher
{
    public event EventHandler? Changed;

    public int Compute(int value) => value * 2;

    [Obsolete("Use Compute instead.")]
    public int LegacyCompute(int value) => value;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

[AttributeUsage(AttributeTargets.All)]
public sealed class MarkerAttribute : Attribute;

public interface INotifier
{
    event EventHandler? Notified;
}
