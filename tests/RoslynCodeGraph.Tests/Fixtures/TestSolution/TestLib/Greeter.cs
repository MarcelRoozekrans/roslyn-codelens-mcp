namespace TestLib;

public class Greeter : IGreeter
{
    public virtual string Greet(string name) => $"Hello, {name}!";
}
