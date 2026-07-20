using RoslynCodeLens.Analysis;

namespace RoslynCodeLens.Tests;

public class ScopePatternTests
{
    [Theory]
    // exact
    [InlineData("Demo.Domain", "Demo.Domain", true)]
    [InlineData("Demo.Domain", "Demo.Domain.Orders", false)]   // exact does NOT match children
    [InlineData("Demo.Domain", "Demo.DomainX", false)]
    // prefix wildcard: the scope itself AND everything beneath it
    [InlineData("Demo.Domain.*", "Demo.Domain", true)]
    [InlineData("Demo.Domain.*", "Demo.Domain.Orders", true)]
    [InlineData("Demo.Domain.*", "Demo.Domain.Orders.Rules", true)]
    [InlineData("Demo.Domain.*", "Demo.DomainX", false)]        // not a segment boundary
    [InlineData("Demo.Domain.*", "Demo.Infrastructure", false)]
    // case sensitivity: C# namespaces are case-sensitive
    [InlineData("Demo.Domain", "demo.domain", false)]
    // match-all
    [InlineData("*", "Anything.At.All", true)]
    [InlineData("*", "", true)]
    public void Matches(string pattern, string scope, bool expected)
        => Assert.Equal(expected, ScopePattern.Matches(pattern, scope));

    [Theory]
    [InlineData("Demo.*.Orders")]   // interior wildcard unsupported
    [InlineData("*.Orders")]        // suffix wildcard unsupported
    [InlineData("Demo.Domain*")]    // wildcard not on a segment boundary
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsMalformedPatterns(string pattern)
        => Assert.False(ScopePattern.IsValid(pattern));

    [Theory]
    [InlineData("Demo.Domain")]
    [InlineData("Demo.Domain.*")]
    [InlineData("*")]
    public void AcceptsWellFormedPatterns(string pattern)
        => Assert.True(ScopePattern.IsValid(pattern));
}
