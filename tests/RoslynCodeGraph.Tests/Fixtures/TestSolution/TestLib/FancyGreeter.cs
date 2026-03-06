namespace TestLib;

public class FancyGreeter : Greeter
{
    public override string Greet(string name) => $"Greetings, {name}!";
}
