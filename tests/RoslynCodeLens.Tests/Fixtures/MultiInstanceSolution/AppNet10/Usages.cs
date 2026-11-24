using Shared;

namespace AppNet10;

[Marker]
public class Usages
{
    public void Subscribe(Publisher publisher) => publisher.Changed += OnChanged;

    public void SubscribeNotifier(INotifier viaInterface, Notifier viaClass)
    {
        viaInterface.Notified += OnChanged;
        viaClass.Notified += OnChanged;
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
