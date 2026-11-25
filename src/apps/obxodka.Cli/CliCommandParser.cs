namespace obxodka.Cli;

//разобранная команда cli с позиционными аргументами и флагами
public sealed record CliParsedCommand(
    string Command,
    IReadOnlyList<string> PositionalArgs,
    IReadOnlyDictionary<string, string?> Options)
{
    public bool HasFlag(string name) => Options.ContainsKey(name);

    public string? GetOption(string name) => Options.TryGetValue(name, out var value) ? value : null;

    public bool IsJson => HasFlag("json");
}

//парсер аргументов командной строки с поддержкой коротких алиасов (start/stop/auth)
public static class CliCommandParser
{
    private static readonly Dictionary<string, string> s_commandAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["start"] = "connect",
        ["up"] = "connect",
        ["stop"] = "disconnect",
        ["down"] = "disconnect",
        ["auth"] = "login",
        ["ls"] = "servers",
        ["st"] = "status",
        ["-h"] = "help",
        ["--help"] = "help",
        ["-v"] = "version",
        ["--version"] = "version"
    };

    private static readonly HashSet<string> s_valueOptions = new(["mtu", "email", "code", "server", "port"], StringComparer.OrdinalIgnoreCase);

    //разбор массива аргументов в нормализованную команду
    public static CliParsedCommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new CliParsedCommand("help", [], new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        }

        var rawCommand = args[0].Trim();
        var normalizedCommand = s_commandAliases.TryGetValue(rawCommand, out var mapped)
            ? mapped
            : rawCommand.ToLowerInvariant();

        List<string> positional = [];
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < args.Length; i++)
        {
            var token = args[i];
            if (token is "-h" or "--help")
            {
                options["help"] = "true";
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal) && token.Length > 2)
            {
                var body = token[2..];
                var eqIdx = body.IndexOf('=');
                if (eqIdx > 0)
                {
                    var key = body[..eqIdx].Trim();
                    var val = body[(eqIdx + 1)..];
                    options[key] = val;
                    continue;
                }

                options[body] = s_valueOptions.Contains(body) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i]
                    : "true";

                continue;
            }

            positional.Add(token);
        }

        return new CliParsedCommand(normalizedCommand, positional, options);
    }
}
