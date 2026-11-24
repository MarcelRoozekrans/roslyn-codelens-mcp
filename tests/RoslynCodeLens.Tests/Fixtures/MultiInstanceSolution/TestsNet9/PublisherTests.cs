using Shared;
using Xunit;

namespace TestsNet9;

public class PublisherTests
{
    [Fact]
    public void Compute_Doubles() => Assert.Equal(4, new Publisher().Compute(2));
}
