using Shared;
using Xunit;

namespace TestsMulti;

// Each method is a test on one target framework only. Whichever instance a tool sees first, the
// other method is a test only in the instance it sees second.
public class CalculatorTests
{
#if NET10_0
	[Fact]
#endif
	public void Halve_OnNet10() => Assert.Equal(2, Calculator.Halve(4));

#if !NET10_0
	[Fact]
#endif
	public void Halve_OnNet9() => Assert.Equal(2, Calculator.Halve(4));
}
