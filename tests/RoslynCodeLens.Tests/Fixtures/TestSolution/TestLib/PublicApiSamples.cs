namespace TestLib;

// Public record class — kind: Record, plus its positional properties (Id, Name) as kind: Property
public record OrderRecord(int Id, string Name);

// Positional record with two same-typed parameters, a construction site, a `with` and a
// deconstruction: the change_signature fixture for primary constructors.
public record ShipmentRecord(int Id, int Weight);

public static class ShipmentRecordUsage
{
    public static ShipmentRecord Create() => new ShipmentRecord(1, 2);

    public static ShipmentRecord Heavier(ShipmentRecord shipment)
        => shipment with { Weight = shipment.Weight + 1 };

    public static int Total(ShipmentRecord shipment)
    {
        var (id, weight) = shipment;
        return id + weight;
    }
}

// Public record struct — kind: RecordStruct, plus positional properties (X, Y)
public record struct PointStruct(int X, int Y);

// Public plain struct — kind: Struct (distinct from RecordStruct)
public struct PlainStruct
{
    public int Value;
}

// Public enum — kind: Enum
public enum PriorityLevel
{
    Low,
    High
}

// Public delegate — kind: Delegate
public delegate void OrderProcessedHandler(int orderId);

// Public abstract class — protected members ARE part of API (subclassable from outside)
public abstract class AbstractProcessor
{
    public void Run() { }
    protected abstract void Process();
    protected int Counter;
}

// Public sealed class — protected members are NOT reachable, must be excluded
public sealed class SealedHolder
{
    public int PublicProp { get; set; }
    protected void HiddenProtected() { }   // EXCLUDED — sealed type's protected is unreachable
}

// Public class with indexer + user-defined operator + event
public class IndexerSample
{
    public string this[int index] => $"Item {index}";

    public static IndexerSample operator +(IndexerSample a, IndexerSample b)
        => new IndexerSample();

    // Public event — kind: Event (provides Event coverage in the API surface)
    public event OrderProcessedHandler? OrderProcessed;
}

// Internal type — entire type and its members must be excluded (not public API)
internal class InternalHidden
{
    public void NotApi() { }
}

// Internal outer with a public nested type — the nested type and its members must NOT appear
// (the nested type is unreachable from outside the assembly).
internal class OuterInternal
{
    public class LeakedNested
    {
        public void LeakedMethod() { }
    }
}

// Public class with overloaded methods — overloads must produce DISTINCT entries (with parameter types).
public class OverloadSample
{
    public void DoSomething(int x) { }
    public void DoSomething(string s) { }
}
