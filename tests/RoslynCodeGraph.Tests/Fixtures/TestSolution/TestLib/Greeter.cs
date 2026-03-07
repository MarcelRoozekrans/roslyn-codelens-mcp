using System;

namespace TestLib;

[Serializable]
public class Greeter : IGreeter
{
    public virtual string Greet(string name) => $"Hello, {name}!";

    [Obsolete("Use Greet instead")]
    public string OldGreet(string name) => Greet(name);

    // Helper method intentionally triggers CA1822 (can be made static)
    public int ComputeLength(string value) => value.Length;
}
