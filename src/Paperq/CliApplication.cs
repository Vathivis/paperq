using System.Text;
using System.Text.Json;

namespace Paperq;

internal static class CliApplication
{
    internal static string ProductVersion { get; } =
        typeof(CliApplication).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    internal static int Run(IReadOnlyList<string> arguments, CliIo io)
    {
        var json = CliInvocation.RequestsJson(arguments);
        try
        {
            var invocation = CliArguments.Parse(arguments);
            json = invocation.Json;
            return Execute(invocation, io);
        }
        catch (PaperqException exception)
        {
            WriteError(io, json, exception.Code, exception.Message, exception.ExitCode);
            return (int)exception.ExitCode;
        }
        catch (UnauthorizedAccessException exception)
        {
            WriteError(io, json, "access_denied", exception.Message, PaperqExitCode.UnexpectedError);
            return (int)PaperqExitCode.UnexpectedError;
        }
        catch (IOException exception)
        {
            WriteError(io, json, "io_error", exception.Message, PaperqExitCode.UnexpectedError);
            return (int)PaperqExitCode.UnexpectedError;
        }
        catch (Exception exception)
        {
            WriteError(io, json, "unexpected_error", exception.Message, PaperqExitCode.UnexpectedError);
            return (int)PaperqExitCode.UnexpectedError;
        }
    }

    private static int Execute(CliInvocation invocation, CliIo io)
    {
        switch (invocation.Command)
        {
            case HelpCommand help:
                return ExecuteHelp(help, invocation.Json, io);
            case VersionCommand:
                return ExecuteVersion(invocation.Json, io);
        }

        var project = ProjectContext.Resolve(invocation.RootPath);
        return invocation.Command switch
        {
            InitCommand command => ExecuteInit(project, command, invocation.Json, io),
            AddCommand command => ExecuteAdd(project, command, invocation.Json, io),
            ListCommand command => ExecuteList(project, command, invocation.Json, io),
            ShowCommand command => ExecuteShow(project, command, invocation.Json, io),
            NextCommand command => ExecuteNext(project, command, invocation.Json, io),
            ResolveCommand command => ExecuteResolve(project, command, invocation.Json, io),
            BlockCommand command => ExecuteBlock(project, command, invocation.Json, io),
            ReopenCommand command => ExecuteReopen(project, command, invocation.Json, io),
            _ => throw new InvalidOperationException("Unsupported command."),
        };
    }

    private static int ExecuteInit(ProjectContext project, InitCommand command, bool json, CliIo io)
    {
        var initializer = new QueueInitializer(project);
        if (command.GitIgnore && !initializer.IsGitRepository)
        {
            throw PaperqException.Usage("--gitignore can only be used when the selected root is a Git repository.");
        }

        initializer.CreateQueue();

        var agentInstructionsAlreadyPresent = initializer.HasAgentInstructions();
        var resolutionReferenceAlreadyPresent = initializer.HasResolutionReference();
        var managedAgentContentAlreadyPresent =
            agentInstructionsAlreadyPresent && resolutionReferenceAlreadyPresent;
        var gitIgnoreAlreadyPresent = initializer.HasGitIgnoreRule();
        var agentInstructionsChanged = false;
        var resolutionReferenceChanged = false;
        var gitIgnoreChanged = false;

        if (!json)
        {
            io.Output.WriteLine($"Queue ready: {initializer.Layout.QueueRoot}");
            io.Output.WriteLine();
            io.Output.WriteLine("Paste-ready AGENTS.md instructions:");
            io.Output.WriteLine();
            io.Output.WriteLine(QueueInitializer.CopyReadyInstructions);
            io.Output.WriteLine();
        }

        if (command.AppendAgents)
        {
            (agentInstructionsChanged, resolutionReferenceChanged) =
                initializer.AppendManagedAgentInstructions();
        }
        else if (!managedAgentContentAlreadyPresent && !json && io.IsInteractive)
        {
            var action = File.Exists(initializer.AgentsPath) ? "Append to" : "Create";
            if (PromptYesNo(io, $"{action} {initializer.AgentsPath}?"))
            {
                (agentInstructionsChanged, resolutionReferenceChanged) =
                    initializer.AppendManagedAgentInstructions();
            }
        }

        if (project.IsGitRepository)
        {
            if (command.GitIgnore)
            {
                gitIgnoreChanged = initializer.AddGitIgnoreRule();
            }
            else if (!gitIgnoreAlreadyPresent && !json && io.IsInteractive &&
                     PromptYesNo(io, $"Add {QueueInitializer.GitIgnoreRule} to {initializer.GitIgnorePath}?"))
            {
                gitIgnoreChanged = initializer.AddGitIgnoreRule();
            }
        }

        var agentsPresent = initializer.HasAgentInstructions();
        var resolutionReferencePresent = initializer.HasResolutionReference();
        var agentsChanged = agentInstructionsChanged || resolutionReferenceChanged;
        var gitIgnorePresent = gitIgnoreAlreadyPresent || gitIgnoreChanged;
        if (json)
        {
            io.Output.WriteLine(JsonOutput.Success("init", writer =>
            {
                writer.WriteString("root", project.RootPath);
                writer.WriteString("queuePath", initializer.Layout.QueueRoot);
                writer.WriteBoolean("gitRepository", project.IsGitRepository);
                writer.WriteString("agentsPath", initializer.AgentsPath);
                writer.WriteBoolean("agentsPresent", agentsPresent);
                writer.WriteBoolean("agentsChanged", agentsChanged);
                writer.WriteBoolean("resolutionReferencePresent", resolutionReferencePresent);
                writer.WriteBoolean("resolutionReferenceChanged", resolutionReferenceChanged);
                writer.WriteString("gitIgnorePath", project.IsGitRepository ? initializer.GitIgnorePath : null);
                writer.WriteBoolean("gitIgnorePresent", gitIgnorePresent);
                writer.WriteBoolean("gitIgnoreChanged", gitIgnoreChanged);
                writer.WriteString("agentInstructions", QueueInitializer.CopyReadyInstructions);
            }));
        }
        else
        {
            io.Output.WriteLine(agentsPresent && resolutionReferencePresent
                ? $"AGENTS.md instructions present: {initializer.AgentsPath}"
                : "AGENTS.md was not changed. Re-run with --append-agents to add the instructions non-interactively.");

            if (project.IsGitRepository)
            {
                io.Output.WriteLine(gitIgnorePresent
                    ? $"Queue ignore rule present: {initializer.GitIgnorePath}"
                    : ".papercuts/ was not added to .gitignore; queue records will be visible to Git.");
            }
        }

        return (int)PaperqExitCode.Success;
    }

    private static int ExecuteAdd(ProjectContext project, AddCommand command, bool json, CliIo io)
    {
        var queue = CreateQueue(project);
        queue.Layout.RequireInitialized();
        var message = command.ReadStdin
            ? InputRules.ReadBounded(io.Input, "message")
            : InputRules.Validate(command.Message!, "message");
        var record = queue.Add(message);

        if (json)
        {
            WriteRecordSuccess(io, "add", record, queue.Layout);
        }
        else
        {
            io.Output.WriteLine($"Added {record.Id} at {queue.Layout.RelativePath(record.State, record.Id)}");
        }

        return (int)PaperqExitCode.Success;
    }

    private static int ExecuteList(ProjectContext project, ListCommand command, bool json, CliIo io)
    {
        var queue = CreateQueue(project);
        var records = queue.List(command.IncludeResolved);
        if (json)
        {
            io.Output.WriteLine(JsonOutput.Success("list", writer =>
            {
                writer.WriteBoolean("includeResolved", command.IncludeResolved);
                writer.WriteStartArray("items");
                foreach (var record in records)
                {
                    writer.WriteStartObject();
                    JsonOutput.WriteRecord(writer, record, queue.Layout);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteNumber("count", records.Count);
            }));
        }
        else if (records.Count == 0)
        {
            io.Output.WriteLine("No papercuts.");
        }
        else
        {
            io.Output.WriteLine("ID                             STATE     MESSAGE");
            foreach (var record in records)
            {
                io.Output.WriteLine($"{record.Id}  {record.State.ToDirectoryName(),-9} {Preview(record.Message, 80)}");
            }
        }

        return (int)PaperqExitCode.Success;
    }

    private static int ExecuteNext(ProjectContext project, NextCommand command, bool json, CliIo io)
    {
        var queue = CreateQueue(project);
        var record = queue.Next(command.Claim);
        if (json)
        {
            WriteRecordSuccess(io, "next", record, queue.Layout, writer =>
                writer.WriteBoolean("claimed", command.Claim));
        }
        else
        {
            if (command.Claim)
            {
                io.Output.WriteLine($"Claimed {record.Id}");
                io.Output.WriteLine();
            }

            WriteRecordHuman(io.Output, record, queue.Layout);
        }

        return (int)PaperqExitCode.Success;
    }

    private static int ExecuteShow(ProjectContext project, ShowCommand command, bool json, CliIo io)
    {
        var queue = CreateQueue(project);
        var record = queue.Show(command.Id);
        if (json)
        {
            WriteRecordSuccess(io, "show", record, queue.Layout, writer =>
                writer.WriteString("history", record.History));
        }
        else
        {
            WriteRecordHuman(io.Output, record, queue.Layout);
        }

        return (int)PaperqExitCode.Success;
    }

    private static int ExecuteResolve(ProjectContext project, ResolveCommand command, bool json, CliIo io)
    {
        var queue = CreateQueue(project);
        queue.Layout.RequireInitialized();
        PapercutId.RequireValid(command.Id);
        var note = command.ReadStdin
            ? InputRules.ReadBounded(io.Input, "note")
            : command.Note!;
        var record = queue.Resolve(command.Id, note);
        if (json)
        {
            WriteRecordSuccess(io, "resolve", record, queue.Layout, writer =>
                writer.WriteString("resolutionPath", queue.Layout.ResolutionJournalRelativePath));
        }
        else
        {
            io.Output.WriteLine($"Resolved {record.Id} at {queue.Layout.RelativePath(record.State, record.Id)}");
            io.Output.WriteLine($"Resolution journal: {queue.Layout.ResolutionJournalRelativePath}");
        }

        return (int)PaperqExitCode.Success;
    }

    private static int ExecuteBlock(ProjectContext project, BlockCommand command, bool json, CliIo io)
    {
        var queue = CreateQueue(project);
        queue.Layout.RequireInitialized();
        PapercutId.RequireValid(command.Id);
        var reason = command.ReadStdin
            ? InputRules.ReadBounded(io.Input, "reason")
            : command.Reason!;
        var record = queue.Block(command.Id, reason);
        return WriteTransitionResult(io, json, "block", "Blocked", record, queue.Layout);
    }

    private static int ExecuteReopen(ProjectContext project, ReopenCommand command, bool json, CliIo io)
    {
        var queue = CreateQueue(project);
        var record = queue.Reopen(command.Id);
        return WriteTransitionResult(io, json, "reopen", "Reopened", record, queue.Layout);
    }

    private static int ExecuteHelp(HelpCommand command, bool json, CliIo io)
    {
        var text = HelpText(command.Topic);
        if (json)
        {
            io.Output.WriteLine(JsonOutput.Success("help", writer =>
            {
                writer.WriteString("topic", command.Topic);
                writer.WriteString("text", text);
            }));
        }
        else
        {
            io.Output.WriteLine(text);
        }

        return (int)PaperqExitCode.Success;
    }

    private static int ExecuteVersion(bool json, CliIo io)
    {
        if (json)
        {
            io.Output.WriteLine(JsonOutput.Success("version", writer =>
                writer.WriteString("version", ProductVersion)));
        }
        else
        {
            io.Output.WriteLine($"paperq {ProductVersion}");
        }

        return (int)PaperqExitCode.Success;
    }

    private static int WriteTransitionResult(
        CliIo io,
        bool json,
        string command,
        string verb,
        PapercutRecord record,
        QueueLayout layout)
    {
        if (json)
        {
            WriteRecordSuccess(io, command, record, layout);
        }
        else
        {
            io.Output.WriteLine($"{verb} {record.Id} at {layout.RelativePath(record.State, record.Id)}");
        }

        return (int)PaperqExitCode.Success;
    }

    private static PaperqQueue CreateQueue(ProjectContext project) => new(project.RootPath);

    private static bool PromptYesNo(CliIo io, string question)
    {
        io.Output.Write($"{question} [y/N] ");
        io.Output.Flush();
        var answer = io.Input.ReadLine();
        return answer is not null &&
               (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
                answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteRecordSuccess(
        CliIo io,
        string command,
        PapercutRecord record,
        QueueLayout layout,
        Action<Utf8JsonWriter>? writeAdditional = null)
    {
        io.Output.WriteLine(JsonOutput.Success(command, writer =>
        {
            JsonOutput.WriteRecord(writer, record, layout);
            writeAdditional?.Invoke(writer);
        }));
    }

    private static void WriteRecordHuman(TextWriter output, PapercutRecord record, QueueLayout layout)
    {
        output.WriteLine($"ID:      {record.Id}");
        output.WriteLine($"State:   {record.State.ToDirectoryName()}");
        output.WriteLine($"Created: {record.CreatedUtc:O}");
        output.WriteLine($"Path:    {layout.RelativePath(record.State, record.Id)}");
        output.WriteLine("Message:");
        output.WriteLine(record.Message);
        if (!string.IsNullOrEmpty(record.History))
        {
            output.WriteLine();
            output.WriteLine("History:");
            output.WriteLine(record.History);
        }
    }

    private static void WriteError(
        CliIo io,
        bool json,
        string code,
        string message,
        PaperqExitCode exitCode)
    {
        if (json)
        {
            io.Output.WriteLine(JsonOutput.Error(code, message, (int)exitCode));
        }
        else
        {
            io.Error.WriteLine($"paperq: {message}");
            if (exitCode == PaperqExitCode.UsageError)
            {
                io.Error.WriteLine("Run 'paperq --help' for usage.");
            }
        }
    }

    private static string Preview(string message, int maximumRunes)
    {
        var firstLineEnd = message.IndexOf('\n');
        var firstLine = firstLineEnd >= 0 ? message[..firstLineEnd] : message;
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in firstLine.EnumerateRunes())
        {
            if (count == maximumRunes - 1)
            {
                builder.Append('…');
                return builder.ToString();
            }

            builder.Append(rune.ToString());
            count++;
        }

        return builder.ToString();
    }

    private static string HelpText(string? topic) => topic switch
    {
        null => """
            paperq - a small, repository-local papercut queue

            Usage:
              paperq init [--append-agents] [--gitignore]
              paperq add <message>
              paperq add --stdin
              paperq list [--all]
              paperq show <id>
              paperq next [--claim]
              paperq resolve <id> (--note <text> | --stdin)
              paperq block <id> (--reason <text> | --stdin)
              paperq reopen <id>

            Global options:
              --root <path>  Use this exact directory instead of Git-root discovery.
              --json         Emit the stable JSON schema on stdout.
              --help, -h     Show help.
              --version      Show the version.

            Global options may appear before or after a command.
            """,
        "init" => """
            Usage: paperq init [--append-agents] [--gitignore]

            Creates .papercuts/open, working, blocked, and resolved. Human-readable use
            prints paste-ready AGENTS.md instructions. Interactive use also offers to add
            those instructions and, inside Git, update .gitignore. Non-interactive use
            changes those files only when the explicit flags are set.
            """,
        "add" => """
            Usage: paperq add <message> | paperq add --stdin

            Creates one Markdown record in .papercuts/open. Input is limited to 64 KiB.
            """,
        "list" => """
            Usage: paperq list [--all]

            Lists open, working, and blocked records. --all also includes resolved records.
            """,
        "show" => """
            Usage: paperq show <id>

            Shows one papercut from any state, including its full message and history.
            """,
        "next" => """
            Usage: paperq next [--claim]

            Selects the oldest open record. --claim atomically moves it to working.
            """,
        "resolve" => """
            Usage: paperq resolve <id> (--note <text> | --stdin)

            Moves a working papercut to resolved and adds its problem and solution to
            PAPERQ_RESOLUTIONS.md for future agents. --stdin reads the note from standard input.
            """,
        "block" => "Usage: paperq block <id> (--reason <text> | --stdin)",
        "reopen" => "Usage: paperq reopen <id>",
        _ => throw PaperqException.Usage($"Unknown help topic: {topic}"),
    };
}
