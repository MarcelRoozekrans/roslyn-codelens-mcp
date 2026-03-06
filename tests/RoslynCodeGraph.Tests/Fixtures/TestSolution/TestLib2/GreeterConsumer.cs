namespace TestLib2;

using TestLib;

public class GreeterConsumer
{
    private readonly IGreeter _greeter;

    public GreeterConsumer(IGreeter greeter)
    {
        _greeter = greeter;
    }

    public string SayHello() => _greeter.Greet("World");
}
