namespace RoslynCodeLens;

/// <summary>
/// Parsed command-line options. Positional arguments are solution paths; flags select
/// the transport. Stdio remains the default so existing MCP client configs keep working.
/// </summary>
public sealed class CliOptions
{
    public const int DefaultPort = 3001;
    public const string DefaultHttpHost = "127.0.0.1";

    public IReadOnlyList<string> SolutionPaths { get; }
    public bool UseHttp { get; }
    public int Port { get; }
    public string HttpHost { get; }

    /// <summary>
    /// Tools to register, from <c>--tools</c>; <see langword="null"/> registers all tools.
    /// </summary>
    public IReadOnlySet<string>? Tools { get; }

    private CliOptions(IReadOnlyList<string> solutionPaths, bool useHttp, int port, string httpHost, IReadOnlySet<string>? tools)
    {
        SolutionPaths = solutionPaths;
        UseHttp = useHttp;
        Port = port;
        HttpHost = httpHost;
        Tools = tools;
    }

    public bool BindsLoopbackOnly =>
        HttpHost is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    public static CliOptions Parse(string[] args)
    {
        var paths = new List<string>();
        var useHttp = false;
        var port = DefaultPort;
        var httpHost = DefaultHttpHost;
        HashSet<string>? tools = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                paths.Add(arg);
                continue;
            }

            var (name, inlineValue) = SplitFlag(arg);
            switch (name)
            {
                case "--http":
                    RejectInlineValue(name, inlineValue);
                    useHttp = true;
                    break;

                case "--port":
                    var portValue = inlineValue ?? NextValue(args, ref i, name);
                    if (!int.TryParse(portValue, out port) || port is < 1 or > 65535)
                        throw new ArgumentException($"Invalid value for --port: '{portValue}'. Expected an integer between 1 and 65535.");
                    break;

                case "--host":
                    httpHost = inlineValue ?? NextValue(args, ref i, name);
                    if (string.IsNullOrWhiteSpace(httpHost))
                        throw new ArgumentException("Invalid value for --host: value is empty.");
                    break;

                case "--tools":
                    tools ??= new HashSet<string>(StringComparer.Ordinal);
                    AddToolNames(inlineValue ?? NextValue(args, ref i, name), tools);
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown option '{arg}'. Supported options: --http, --port <1-65535>, --host <address>, " +
                        "--tools <name>[,<name>...]. Positional arguments are .sln/.slnx paths.");
            }
        }

        return new CliOptions(paths, useHttp, port, httpHost, tools);
    }

    private static void AddToolNames(string value, HashSet<string> tools)
    {
        var names = value.Split(',', StringSplitOptions.TrimEntries);
        if (names.Any(n => n.Length == 0))
            throw new ArgumentException($"Invalid value for --tools: '{value}'. Expected a comma-separated list of tool names.");

        var unknown = names.Where(n => !ToolSelection.KnownToolNames.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"Unknown tool(s) for --tools: {string.Join(", ", unknown)}.");

        tools.UnionWith(names);
    }

    private static (string Name, string? InlineValue) SplitFlag(string arg)
    {
        var eq = arg.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? (arg, null) : (arg[..eq], arg[(eq + 1)..]);
    }

    private static void RejectInlineValue(string name, string? inlineValue)
    {
        if (inlineValue is not null)
            throw new ArgumentException($"Option '{name}' does not take a value.");
    }

    private static string NextValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Option '{name}' requires a value.");
        return args[++i];
    }
}
