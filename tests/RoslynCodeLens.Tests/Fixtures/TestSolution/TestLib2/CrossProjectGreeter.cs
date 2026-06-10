using TestLib;

namespace TestLib2;

public class CrossProjectGreeter : IGreeter, ICrossProjectOnly
{
	public string Greet(string name) => $"Cross-project hello, {name}!";
	public string Execute() => Greet("World");
}
