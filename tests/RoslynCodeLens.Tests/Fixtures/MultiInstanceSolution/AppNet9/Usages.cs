using Shared;

namespace AppNet9;

[Marker]
public class Usages
{
    public void Subscribe(Publisher publisher) => publisher.Changed += OnChanged;

    public void SubscribeNotifier(INotifier viaInterface, Notifier viaClass)
    {
        viaInterface.Notified += OnChanged;
        viaClass.Notified += OnChanged;
    }

    // DualNotifier.Notified is NOT the INotifier.Notified implementation.
    public void SubscribeDual(DualNotifier dual)
    {
        dual.Notified += OnChanged;
    }

#pragma warning disable CS0618 // The obsolete call is what the fixture is about.
    public int CallLegacy(Publisher publisher) => publisher.LegacyCompute(1);
#pragma warning restore CS0618

    public int CallLegacyByRef(Publisher publisher, int value) => publisher.LegacyCompute(ref value);

    private void OnChanged(object? sender, EventArgs e) { }
}

public class Notifier : INotifier
{
    public event EventHandler? Notified;

    public void Raise() => Notified?.Invoke(this, EventArgs.Empty);
}

// Implements INotifier explicitly and declares an unrelated public event of the same name.
public class DualNotifier : INotifier
{
    event EventHandler? INotifier.Notified { add { } remove { } }

    public event EventHandler? Notified;

    public void Raise() => Notified?.Invoke(this, EventArgs.Empty);
}
