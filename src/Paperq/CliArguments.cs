namespace Paperq;

internal sealed record CliInvocation(bool Json, string? RootPath, CliCommand Command)
{
    internal static bool RequestsJson(IReadOnlyList<string> arguments)
    {
        var literal = false;
        foreach (var argument in arguments)
        {
            if (argument == "--")
            {
                literal = true;
                continue;
            }

            if (!literal && argument == "--json")
            {
                return true;
            }
        }

        return false;
    }
}

internal abstract record CliCommand;

internal sealed record HelpCommand(string? Topic) : CliCommand;

internal sealed record VersionCommand : CliCommand;

internal sealed record InitCommand(bool AppendAgents, bool GitIgnore) : CliCommand;

internal sealed record AddCommand(string? Message, bool ReadStdin) : CliCommand;

internal sealed record ListCommand(bool IncludeResolved) : CliCommand;

internal sealed record ShowCommand(string Id) : CliCommand;

internal sealed record NextCommand(bool Claim) : CliCommand;

internal sealed record ResolveCommand(string Id, string? Note, bool ReadStdin) : CliCommand;

internal sealed record BlockCommand(string Id, string? Reason, bool ReadStdin) : CliCommand;

internal sealed record ReopenCommand(string Id) : CliCommand;

internal static class CliArguments
{
    internal static CliInvocation Parse(IReadOnlyList<string> arguments)
    {
        var globals = new GlobalOptions();
        var index = 0;
        while (index < arguments.Count && TryConsumeGlobal(arguments, ref index, globals))
        {
        }

        if (index >= arguments.Count)
        {
            return globals.Create(new HelpCommand(null));
        }

        var commandName = arguments[index++];
        if (commandName is "--help" or "-h" or "help")
        {
            string? topic = null;
            while (index < arguments.Count)
            {
                if (TryConsumeGlobal(arguments, ref index, globals))
                {
                    continue;
                }

                if (topic is not null)
                {
                    throw PaperqException.Usage("The help command accepts at most one command name.");
                }

                topic = arguments[index++];
            }

            return globals.Create(new HelpCommand(topic));
        }

        if (commandName == "--version")
        {
            while (index < arguments.Count)
            {
                if (!TryConsumeGlobal(arguments, ref index, globals))
                {
                    throw PaperqException.Usage("--version does not accept arguments.");
                }
            }

            return globals.Create(new VersionCommand());
        }

        CliCommand command;
        try
        {
            command = commandName switch
            {
                "init" => ParseInit(arguments, ref index, globals),
                "add" => ParseAdd(arguments, ref index, globals),
                "list" => ParseList(arguments, ref index, globals),
                "show" => ParseShow(arguments, ref index, globals),
                "next" => ParseNext(arguments, ref index, globals),
                "resolve" => ParseResolve(arguments, ref index, globals),
                "block" => ParseBlock(arguments, ref index, globals),
                "reopen" => ParseReopen(arguments, ref index, globals),
                _ => throw PaperqException.Usage($"Unknown command: {commandName}"),
            };
        }
        catch (HelpRequestedException exception)
        {
            command = new HelpCommand(exception.Topic);
        }

        return globals.Create(command);
    }

    private static CliCommand ParseInit(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals)
    {
        var appendAgents = false;
        var gitIgnore = false;
        while (index < arguments.Count)
        {
            if (TryConsumeGlobal(arguments, ref index, globals))
            {
                continue;
            }

            switch (arguments[index++])
            {
                case "--append-agents":
                    appendAgents = true;
                    break;
                case "--gitignore":
                    gitIgnore = true;
                    break;
                case "--help":
                case "-h":
                    return new HelpCommand("init");
                default:
                    throw PaperqException.Usage("Usage: paperq init [--append-agents] [--gitignore]");
            }
        }

        return new InitCommand(appendAgents, gitIgnore);
    }

    private static CliCommand ParseAdd(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals)
    {
        var stdin = false;
        var positional = new List<string>();
        var literal = false;
        while (index < arguments.Count)
        {
            if (!literal && arguments[index] == "--")
            {
                literal = true;
                index++;
                continue;
            }

            if (!literal && arguments[index] == "--stdin")
            {
                stdin = true;
                index++;
                continue;
            }

            if (!literal && arguments[index] is "--help" or "-h")
            {
                index++;
                return new HelpCommand("add");
            }

            if (!literal && TryConsumeGlobal(arguments, ref index, globals))
            {
                continue;
            }

            if (!literal && arguments[index].StartsWith("-", StringComparison.Ordinal))
            {
                throw PaperqException.Usage($"Unknown add option: {arguments[index]}");
            }

            positional.Add(arguments[index++]);
        }

        if (stdin == (positional.Count > 0) || positional.Count > 1)
        {
            throw PaperqException.Usage("Usage: paperq add <message> | paperq add --stdin");
        }

        return new AddCommand(positional.Count == 1 ? positional[0] : null, stdin);
    }

    private static CliCommand ParseList(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals)
    {
        var includeResolved = false;
        while (index < arguments.Count)
        {
            if (TryConsumeGlobal(arguments, ref index, globals))
            {
                continue;
            }

            switch (arguments[index++])
            {
                case "--all":
                    includeResolved = true;
                    break;
                case "--help":
                case "-h":
                    return new HelpCommand("list");
                default:
                    throw PaperqException.Usage("Usage: paperq list [--all]");
            }
        }

        return new ListCommand(includeResolved);
    }

    private static CliCommand ParseNext(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals)
    {
        var claim = false;
        while (index < arguments.Count)
        {
            if (TryConsumeGlobal(arguments, ref index, globals))
            {
                continue;
            }

            switch (arguments[index++])
            {
                case "--claim":
                    claim = true;
                    break;
                case "--help":
                case "-h":
                    return new HelpCommand("next");
                default:
                    throw PaperqException.Usage("Usage: paperq next [--claim]");
            }
        }

        return new NextCommand(claim);
    }

    private static CliCommand ParseShow(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals) =>
        new ShowCommand(ParseId(arguments, ref index, globals, "show"));

    private static CliCommand ParseResolve(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals)
    {
        var parsed = ParseIdAndValue(arguments, ref index, globals, "--note", "resolve");
        return new ResolveCommand(parsed.Id, parsed.Value, parsed.ReadStdin);
    }

    private static CliCommand ParseBlock(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals)
    {
        var parsed = ParseIdAndValue(arguments, ref index, globals, "--reason", "block");
        return new BlockCommand(parsed.Id, parsed.Value, parsed.ReadStdin);
    }

    private static CliCommand ParseReopen(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals)
        => new ReopenCommand(ParseId(arguments, ref index, globals, "reopen"));

    private static string ParseId(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals,
        string commandName)
    {
        string? id = null;
        var literal = false;
        while (index < arguments.Count)
        {
            if (!literal && arguments[index] == "--")
            {
                literal = true;
                index++;
                continue;
            }

            if (!literal && arguments[index] is "--help" or "-h")
            {
                index++;
                throw new HelpRequestedException(commandName);
            }

            if (!literal && TryConsumeGlobal(arguments, ref index, globals))
            {
                continue;
            }

            if (!literal && arguments[index].StartsWith("-", StringComparison.Ordinal))
            {
                throw PaperqException.Usage($"Unknown {commandName} option: {arguments[index]}");
            }

            if (id is not null)
            {
                throw PaperqException.Usage($"Usage: paperq {commandName} <id>");
            }

            id = arguments[index++];
        }

        return id ?? throw PaperqException.Usage($"Usage: paperq {commandName} <id>");
    }

    private static (string Id, string? Value, bool ReadStdin) ParseIdAndValue(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals,
        string optionName,
        string commandName)
    {
        string? id = null;
        string? value = null;
        var stdin = false;
        var literal = false;
        var usage = $"Usage: paperq {commandName} <id> ({optionName} <text> | --stdin)";
        while (index < arguments.Count)
        {
            if (!literal && arguments[index] == "--")
            {
                literal = true;
                index++;
                continue;
            }

            if (!literal && arguments[index] is "--help" or "-h")
            {
                index++;
                throw new HelpRequestedException(commandName);
            }

            if (!literal && arguments[index] == "--stdin")
            {
                if (stdin)
                {
                    throw PaperqException.Usage("--stdin can only be supplied once.");
                }

                stdin = true;
                index++;
                continue;
            }

            if (!literal && TryConsumeValueOption(arguments, ref index, optionName, out var optionValue))
            {
                if (value is not null)
                {
                    throw PaperqException.Usage($"{optionName} can only be supplied once.");
                }

                value = optionValue;
                continue;
            }

            if (!literal && TryConsumeGlobal(arguments, ref index, globals))
            {
                continue;
            }

            if (!literal && arguments[index].StartsWith("-", StringComparison.Ordinal))
            {
                throw PaperqException.Usage($"Unknown {commandName} option: {arguments[index]}");
            }

            if (id is not null)
            {
                throw PaperqException.Usage(usage);
            }

            id = arguments[index++];
        }

        if (id is null || stdin == (value is not null))
        {
            throw PaperqException.Usage(usage);
        }

        return (id, value, stdin);
    }

    private static bool TryConsumeValueOption(
        IReadOnlyList<string> arguments,
        ref int index,
        string name,
        out string value)
    {
        var argument = arguments[index];
        if (argument.StartsWith(name + '=', StringComparison.Ordinal))
        {
            value = argument[(name.Length + 1)..];
            index++;
            return true;
        }

        if (argument != name)
        {
            value = string.Empty;
            return false;
        }

        if (++index >= arguments.Count)
        {
            throw PaperqException.Usage($"{name} requires a value.");
        }

        value = arguments[index++];
        return true;
    }

    private static bool TryConsumeGlobal(
        IReadOnlyList<string> arguments,
        ref int index,
        GlobalOptions globals)
    {
        var argument = arguments[index];
        if (argument == "--json")
        {
            globals.Json = true;
            index++;
            return true;
        }

        if (argument.StartsWith("--root=", StringComparison.Ordinal))
        {
            globals.SetRoot(argument[7..]);
            index++;
            return true;
        }

        if (argument != "--root")
        {
            return false;
        }

        if (++index >= arguments.Count)
        {
            throw PaperqException.Usage("--root requires a directory path.");
        }

        globals.SetRoot(arguments[index++]);
        return true;
    }

    private sealed class GlobalOptions
    {
        internal bool Json { get; set; }

        internal string? RootPath { get; private set; }

        internal void SetRoot(string value)
        {
            if (RootPath is not null)
            {
                throw PaperqException.Usage("--root can only be supplied once.");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                throw PaperqException.Usage("--root requires a non-empty directory path.");
            }

            RootPath = value;
        }

        internal CliInvocation Create(CliCommand command) => new(Json, RootPath, command);
    }

    private sealed class HelpRequestedException(string topic) : Exception
    {
        internal string Topic { get; } = topic;
    }
}
