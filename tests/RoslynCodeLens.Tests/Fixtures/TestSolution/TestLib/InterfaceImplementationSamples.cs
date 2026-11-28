namespace TestLib;

// Every call below binds to the interface member, never to the implementing class member,
// so the implementations are only reachable through the interface.
public interface IShape
{
    double Area { get; }

    double Scale(double factor);

    event EventHandler? Changed;
}

public sealed class Square : IShape
{
    public double Area => 1;

    public double Scale(double factor) => factor;

    public event EventHandler? Changed { add { } remove { } }

    public double NotPartOfTheShapeContract() => 0;
}

// Implements an interface from metadata; nothing calls Dispose in the solution.
public sealed class SquareResource : IDisposable
{
    public void Dispose() { }
}

public static class ShapeConsumer
{
    public static double Measure(IShape shape)
    {
        shape.Changed += (_, _) => { };
        return shape.Area + shape.Scale(2);
    }

    public static double MeasureSquare() => Measure(new Square());

    public static void Release() => new SquareResource().ToString();
}
