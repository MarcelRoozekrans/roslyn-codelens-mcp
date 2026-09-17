using PropsLib;
using Xunit;

namespace PropsDrivenTests;

public class WidgetTests
{
    [Fact]
    public void DescribeContainsName()
    {
        var widget = new Widget();
        var result = widget.Describe("gizmo");
        Assert.Contains("gizmo", result);
    }
}
