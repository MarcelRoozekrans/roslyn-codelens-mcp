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
        Assert.Null(options.Tools);
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
    [InlineData("--tools find_callers,go_to_definition")]
    [InlineData("--tools=find_callers,go_to_definition")]
    public void Parse_Tools_BothSyntaxes(string argLine)
    {
        var options = CliOptions.Parse(["my.sln", .. argLine.Split(' ')]);

        Assert.Equal(["my.sln"], options.SolutionPaths);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "find_callers", "go_to_definition" }, options.Tools);
    }

    [Fact]
    public void Parse_Tools_RepeatedWithSpaces_AreMerged()
    {
        var options = CliOptions.Parse(["--tools", "find_callers, go_to_definition", "--tools", "find_callers"]);

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "find_callers", "go_to_definition" }, options.Tools);
    }

    [Fact]
    public void Parse_Tools_UnknownNames_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--tools", "find_callers,no_such_tool,Find_Callers"]));
        Assert.Equal("Unknown tool(s) for --tools: no_such_tool, Find_Callers.", ex.Message);
    }

    [Theory]
    [InlineData("--tools=")]
    [InlineData("--tools=find_callers,")]
    [InlineData("--tools=find_callers,,go_to_definition")]
    public void Parse_Tools_EmptyName_Throws(string arg)
    {
        var ex = Assert.Throws<ArgumentException>(() => CliOptions.Parse([arg]));
        Assert.StartsWith("Invalid value for --tools:", ex.Message);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--host")]
    [InlineData("--tools")]
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
