namespace Duplicate;

// Declared with the same full name in AppNet9 and AppNet10, each with its own member.
[Marker]
public class Helper : Shared.IRunner
{
    public void RunNet10() { }
}

// Declared with the same full name in AppNet9 and AppNet10.
[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkerAttribute : Attribute;
