namespace RoslynCodeLens.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Parse_NoArgs_DefaultsToStdio()
    {
        var options = CliOptions.Parse([]);

        Assert.Empty(options.SolutionPaths);
        Assert.False(options.UseHttp);
        Assert.Equal(CliOptions.DefaultPort, options.Port);
        Assert.Equal(CliOptions.DefaultHttpHost, options.HttpHost);
    }

    [Fact]
    public void Parse_PositionalArgs_AreSolutionPaths()
    {
        var options = CliOptions.Parse(["a.sln", @"c:\repo\b.slnx"]);

        Assert.Equal(["a.sln", @"c:\repo\b.slnx"], options.SolutionPaths);
        Assert.False(options.UseHttp);
    }

    [Fact]
    public void Parse_HttpFlag_EnablesHttpWithDefaults()
    {
        var options = CliOptions.Parse(["--http"]);

        Assert.True(options.UseHttp);
        Assert.Equal(CliOptions.DefaultPort, options.Port);
        Assert.Equal(CliOptions.DefaultHttpHost, options.HttpHost);
        Assert.True(options.BindsLoopbackOnly);
    }

    [Theory]
    [InlineData("--http --port 8080")]
    [InlineData("--http --port=8080")]
    public void Parse_Port_BothSyntaxes(string argLine)
    {
        var options = CliOptions.Parse(argLine.Split(' '));

        Assert.True(options.UseHttp);
        Assert.Equal(8080, options.Port);
    }

    [Fact]
    public void Parse_MixedPathsAndFlags_KeepsBoth()
    {
        var options = CliOptions.Parse(["my.sln", "--http", "--port", "9000", "other.sln"]);

        Assert.Equal(["my.sln", "other.sln"], options.SolutionPaths);
        Assert.True(options.UseHttp);
        Assert.Equal(9000, options.Port);
    }

    [Fact]
    public void Parse_NonLoopbackHost_IsFlagged()
    {
        var options = CliOptions.Parse(["--http", "--host", "0.0.0.0"]);

        Assert.Equal("0.0.0.0", options.HttpHost);
        Assert.False(options.BindsLoopbackOnly);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void Parse_LoopbackHosts_AreLoopback(string host)
    {
        var options = CliOptions.Parse(["--http", $"--host={host}"]);

        Assert.True(options.BindsLoopbackOnly);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    public void Parse_InvalidPort_Throws(string port)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--port", port]));
        Assert.Contains("--port", ex.Message);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--host")]
    public void Parse_MissingValue_Throws(string flag)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliOptions.Parse([flag]));
        Assert.Contains("requires a value", ex.Message);
    }

    [Fact]
    public void Parse_UnknownFlag_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--verbose"]));
        Assert.Contains("--verbose", ex.Message);
    }

    [Fact]
    public void Parse_HttpWithInlineValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--http=yes"]));
    }
}
