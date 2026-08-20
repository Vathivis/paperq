using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Paperq.Tests;

internal static class TestSuite
{
    private static readonly (string Name, Action Test)[] Tests =
    [
        ("root discovery and exact override", RootDiscoveryAndOverride),
        ("non-interactive init is conservative", NonInteractiveInitIsConservative),
        ("interactive init updates files idempotently", InteractiveInitIsIdempotent),
        ("init migrates legacy AGENTS without changing custom instructions", InitMigratesLegacyAgents),
        ("gitignore flag requires Git", GitIgnoreRequiresGit),
        ("add and JSON list preserve Markdown", AddAndList),
        ("show returns a full record and history", ShowReturnsFullRecord),
        ("input safety limits", InputSafetyLimits),
        ("lifecycle text supports stdin", LifecycleTextSupportsStdin),
        ("global options are order-independent", GlobalOptionsAreOrderIndependent),
        ("lifecycle errors use consistent preconditions", LifecyclePreconditionsAreConsistent),
        ("FIFO selection and claiming", FifoSelectionAndClaiming),
        ("concurrent claims are unique", ConcurrentClaimsAreUnique),
        ("concurrent lifecycle transitions have one winner", ConcurrentLifecycleTransitionsHaveOneWinner),
        ("block, reopen, and resolve lifecycle", LifecycleTransitions),
        ("resolution journal retries are idempotent", ResolutionJournalRetriesAreIdempotent),
        ("concurrent resolutions share one journal", ConcurrentResolutionsShareOneJournal),
        ("existing non-paperq resolution document is preserved", ExistingResolutionDocumentIsPreserved),
        ("resolve JSON identifies the resolution journal", ResolveJsonIdentifiesJournal),
        ("duplicate states are rejected", DuplicateStatesAreRejected),
        ("JSON errors stay on stdout", JsonErrorsStayOnStdout),
    ];

    internal static int Run()
    {
        var failures = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void RootDiscoveryAndOverride()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, ".git"));
        var nested = Path.Combine(directory.Path, "one", "two");
        Directory.CreateDirectory(nested);

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = nested;
            var discovered = ProjectContext.Resolve(null);
            Assert.Equal(directory.Path, discovered.RootPath);
            Assert.True(discovered.IsGitRepository);

            var exact = ProjectContext.Resolve(nested);
            Assert.Equal(nested, exact.RootPath);
            Assert.False(exact.IsGitRepository);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    private static void NonInteractiveInitIsConservative()
    {
        using var directory = new TestDirectory(git: true);
        var result = RunCli(directory.Path, ["init"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Paste-ready AGENTS.md instructions", result.Output);
        Assert.Contains("after the record is added, end the papercut-capture side-track and continue the main task", result.Output);
        Assert.Contains("When explicitly assigned papercut maintenance", result.Output);
        Assert.Contains("If `PAPERQ_RESOLUTIONS.md` exists", result.Output);

        foreach (var state in new[] { "open", "working", "blocked", "resolved" })
        {
            Assert.True(Directory.Exists(Path.Combine(directory.Path, ".papercuts", state)));
        }

        Assert.False(File.Exists(Path.Combine(directory.Path, "AGENTS.md")));
        Assert.False(File.Exists(Path.Combine(directory.Path, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(directory.Path, QueueLayout.ResolutionJournalFileName)));

        var jsonResult = RunCli(directory.Path, ["init", "--json"]);
        Assert.Equal(0, jsonResult.ExitCode);
        using var document = JsonDocument.Parse(jsonResult.Output);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(document.RootElement.GetProperty("data").GetProperty("agentsChanged").GetBoolean());
        Assert.False(document.RootElement.GetProperty("data").GetProperty("resolutionReferencePresent").GetBoolean());
        Assert.False(document.RootElement.GetProperty("data").GetProperty("resolutionReferenceChanged").GetBoolean());
    }

    private static void InteractiveInitIsIdempotent()
    {
        using var directory = new TestDirectory(git: true);
        var first = RunCli(directory.Path, ["init"], "y\ny\n", interactive: true);
        Assert.Equal(0, first.ExitCode);

        var agentsPath = Path.Combine(directory.Path, "AGENTS.md");
        var ignorePath = Path.Combine(directory.Path, ".gitignore");
        Assert.True(File.Exists(agentsPath));
        Assert.True(File.Exists(ignorePath));
        Assert.Contains(QueueInitializer.AgentsStartMarker, File.ReadAllText(agentsPath));
        Assert.Contains(QueueInitializer.ResolutionReferenceStartMarker, File.ReadAllText(agentsPath));
        Assert.Contains(QueueInitializer.GitIgnoreRule, File.ReadAllText(ignorePath));

        var second = RunCli(directory.Path, ["init", "--append-agents", "--gitignore"]);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(1, CountOccurrences(File.ReadAllText(agentsPath), QueueInitializer.AgentsStartMarker));
        Assert.Equal(1, CountOccurrences(File.ReadAllText(agentsPath), QueueInitializer.ResolutionReferenceStartMarker));
        Assert.Equal(1, CountOccurrences(File.ReadAllText(ignorePath), QueueInitializer.GitIgnoreRule));
    }

    private static void InitMigratesLegacyAgents()
    {
        using var directory = new TestDirectory(git: true);
        var agentsPath = Path.Combine(directory.Path, "AGENTS.md");
        var customText = "# Project instructions\n\nKeep this custom guidance.\n";
        File.WriteAllText(agentsPath, customText + "\n" + QueueInitializer.AgentInstructions + "\n");

        var result = RunCli(directory.Path, ["init", "--append-agents"]);
        Assert.Equal(0, result.ExitCode);

        var actual = File.ReadAllText(agentsPath);
        Assert.True(actual.StartsWith(customText, StringComparison.Ordinal));
        Assert.Contains("Keep this custom guidance.", actual);
        Assert.Contains("PAPERQ_RESOLUTIONS.md", actual);
        Assert.Equal(1, CountOccurrences(actual, QueueInitializer.AgentsStartMarker));
        Assert.Equal(1, CountOccurrences(actual, QueueInitializer.ResolutionReferenceStartMarker));
    }

    private static void GitIgnoreRequiresGit()
    {
        using var directory = new TestDirectory();
        var result = RunCli(directory.Path, ["init", "--gitignore"]);
        Assert.Equal((int)PaperqExitCode.UsageError, result.ExitCode);
        Assert.Contains("only be used", result.Error);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, ".papercuts")));
    }

    private static void AddAndList()
    {
        using var directory = new TestDirectory(git: true);
        Assert.Equal(0, RunCli(directory.Path, ["init"]).ExitCode);

        var add = RunCli(directory.Path, ["add", "A flaky command needs a retry."]);
        Assert.Equal(0, add.ExitCode);
        var openFiles = Directory.GetFiles(Path.Combine(directory.Path, ".papercuts", "open"), "*.md");
        Assert.Equal(1, openFiles.Length);
        var markdown = File.ReadAllText(openFiles[0]);
        Assert.Contains("# Papercut", markdown);
        Assert.Contains("## Message\n\nA flaky command needs a retry.", markdown);
        Assert.Contains(RecordCodec.EventsMarker, markdown);

        var stdinAdd = RunCli(directory.Path, ["add", "--stdin"], "First line\r\nSecond line\r\n");
        Assert.Equal(0, stdinAdd.ExitCode);

        var list = RunCli(directory.Path, ["list", "--json"]);
        Assert.Equal(0, list.ExitCode);
        Assert.Equal(string.Empty, list.Error);
        using var document = JsonDocument.Parse(list.Output);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("count").GetInt32());
        var items = data.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("open", items[0].GetProperty("state").GetString());
        Assert.True(items[1].GetProperty("message").GetString()!.Contains("\n", StringComparison.Ordinal));
    }

    private static void ShowReturnsFullRecord()
    {
        using var directory = new TestDirectory();
        Assert.Equal(0, RunCli(directory.Path, ["init"]).ExitCode);
        var queue = new PaperqQueue(directory.Path);
        var record = queue.Add("First line\nSecond line with full detail.");
        queue.Next(claim: true);
        queue.Block(record.Id, "Waiting for a reproducible environment.");

        var human = RunCli(directory.Path, ["show", record.Id]);
        Assert.Equal(0, human.ExitCode);
        Assert.Contains("State:   blocked", human.Output);
        Assert.Contains("Second line with full detail.", human.Output);
        Assert.Contains("History:\n### Blocked", InputRules.NormalizeLineEndings(human.Output));
        Assert.Contains("Waiting for a reproducible environment.", human.Output);

        var json = RunCli(directory.Path, ["show", "--json", record.Id]);
        Assert.Equal(0, json.ExitCode);
        using var document = JsonDocument.Parse(json.Output);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("blocked", data.GetProperty("state").GetString());
        Assert.True(data.GetProperty("message").GetString()!.Contains('\n', StringComparison.Ordinal));
        Assert.Contains("### Blocked", data.GetProperty("history").GetString()!);
    }

    private static void InputSafetyLimits()
    {
        using var directory = new TestDirectory();
        Assert.Equal(0, RunCli(directory.Path, ["init"]).ExitCode);

        Assert.Equal(
            (int)PaperqExitCode.InvalidData,
            RunCli(directory.Path, ["add", "bad\0value"]).ExitCode);
        Assert.Equal(
            (int)PaperqExitCode.InvalidData,
            RunCli(directory.Path, ["add", new string('a', InputRules.MaxInputBytes + 1)]).ExitCode);
        Assert.Equal(
            (int)PaperqExitCode.InvalidData,
            RunCli(directory.Path, ["add", "\ud800"]).ExitCode);
        Assert.Equal(
            (int)PaperqExitCode.InvalidData,
            RunCli(directory.Path, ["add", RecordCodec.EventsMarker]).ExitCode);
    }

    private static void LifecycleTextSupportsStdin()
    {
        using var directory = new TestDirectory();
        Assert.Equal(0, RunCli(directory.Path, ["init"]).ExitCode);
        var queue = new PaperqQueue(directory.Path);
        var record = queue.Add("Lifecycle stdin");
        queue.Next(claim: true);

        var blocked = RunCli(
            directory.Path,
            ["block", record.Id, "--stdin"],
            "First blocking line\r\nSecond blocking line\r\n");
        Assert.Equal(0, blocked.ExitCode);
        Assert.Contains(
            "First blocking line\nSecond blocking line",
            InputRules.NormalizeLineEndings(File.ReadAllText(
                queue.Layout.RecordPath(QueueState.Blocked, record.Id))));

        Assert.Equal(0, RunCli(directory.Path, ["reopen", record.Id]).ExitCode);
        Assert.Equal(0, RunCli(directory.Path, ["next", "--claim"]).ExitCode);
        var resolved = RunCli(
            directory.Path,
            ["resolve", record.Id, "--stdin", "--json"],
            "Verified from stdin.\r\nKept multiline evidence.\r\n");
        Assert.Equal(0, resolved.ExitCode);
        Assert.Contains(
            "Verified from stdin.\nKept multiline evidence.",
            InputRules.NormalizeLineEndings(File.ReadAllText(queue.Layout.ResolutionJournalPath)));

        var conflicting = RunCli(
            directory.Path,
            ["resolve", record.Id, "--note", "inline", "--stdin"],
            "stdin");
        Assert.Equal((int)PaperqExitCode.UsageError, conflicting.ExitCode);
    }

    private static void GlobalOptionsAreOrderIndependent()
    {
        using var directory = new TestDirectory();

        var help = RunCli(directory.Path, ["help", "--json", "add"]);
        Assert.Equal(0, help.ExitCode);
        using (var document = JsonDocument.Parse(help.Output))
        {
            Assert.Equal("help", document.RootElement.GetProperty("command").GetString());
            Assert.Equal("add", document.RootElement.GetProperty("data").GetProperty("topic").GetString());
        }

        var version = RunCli(directory.Path, ["--version", "--json"]);
        Assert.Equal(0, version.ExitCode);
        using var versionDocument = JsonDocument.Parse(version.Output);
        Assert.Equal("1.0.0", versionDocument.RootElement.GetProperty("data").GetProperty("version").GetString());
    }

    private static void LifecyclePreconditionsAreConsistent()
    {
        using var directory = new TestDirectory();
        const string id = "20260805T142301123Z-a7f3c921de";

        var blocked = RunCli(directory.Path, ["block", id, "--reason", ""]);
        var resolved = RunCli(directory.Path, ["resolve", id, "--note", ""]);
        var blockedStdin = RunCli(directory.Path, ["block", id, "--stdin"]);
        var resolvedStdin = RunCli(directory.Path, ["resolve", id, "--stdin"]);
        Assert.Equal((int)PaperqExitCode.NotInitialized, blocked.ExitCode);
        Assert.Equal(resolved.ExitCode, blocked.ExitCode);
        Assert.Equal(blocked.ExitCode, blockedStdin.ExitCode);
        Assert.Equal(resolved.ExitCode, resolvedStdin.ExitCode);
    }

    private static void FifoSelectionAndClaiming()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var clock = new SequenceTimeProvider(
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 5, 10, 0, 1, TimeSpan.Zero));
        var queue = new PaperqQueue(directory.Path, clock);
        var first = queue.Add("first");
        var second = queue.Add("second");

        Assert.Equal(first.Id, queue.Next(claim: false).Id);
        Assert.True(File.Exists(first.FullPath));

        var claimedFirst = queue.Next(claim: true);
        Assert.Equal(first.Id, claimedFirst.Id);
        Assert.Equal(QueueState.Working, claimedFirst.State);
        Assert.False(File.Exists(first.FullPath));

        var claimedSecond = queue.Next(claim: true);
        Assert.Equal(second.Id, claimedSecond.Id);
    }

    private static void ConcurrentClaimsAreUnique()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var setupQueue = new PaperqQueue(directory.Path);
        const int count = 16;
        for (var index = 0; index < count; index++)
        {
            setupQueue.Add($"item {index}");
        }

        var claimed = new ConcurrentBag<string>();
        var tasks = Enumerable.Range(0, count)
            .Select(_ => Task.Run(() =>
            {
                var queue = new PaperqQueue(directory.Path);
                claimed.Add(queue.Next(claim: true).Id);
            }))
            .ToArray();
        Task.WaitAll(tasks);

        Assert.Equal(count, claimed.Count);
        var distinct = claimed.Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(
            distinct.Length == count,
            $"Expected {count} distinct claims, got {distinct.Length}: {string.Join(", ", claimed)}");
        Assert.Equal(0, Directory.GetFiles(Path.Combine(directory.Path, ".papercuts", "open"), "*.md").Length);
        Assert.Equal(count, Directory.GetFiles(Path.Combine(directory.Path, ".papercuts", "working"), "*.md").Length);
    }

    private static void LifecycleTransitions()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var added = queue.Add("The cache is stale.");
        queue.Next(claim: true);

        const string reason = "Needs access to the remote service.";
        var workingPath = queue.Layout.RecordPath(QueueState.Working, added.Id);
        using (var stream = TextFile.OpenRecordForTransition(workingPath))
        {
            TextFile.AppendUtf8(stream, RecordCodec.FormatEvent("Blocked", reason), workingPath);
        }

        var blocked = queue.Block(added.Id, reason);
        Assert.Equal(QueueState.Blocked, blocked.State);
        Assert.Contains("### Blocked\n\nNeeds access", File.ReadAllText(blocked.FullPath));
        Assert.Equal(1, CountOccurrences(blocked.History, "### Blocked"));

        var reopened = queue.Reopen(added.Id);
        Assert.Equal(QueueState.Open, reopened.State);
        Assert.Contains("### Reopened", File.ReadAllText(reopened.FullPath));

        queue.Next(claim: true);
        var resolved = queue.Resolve(added.Id, "Cleared and documented the cache.");
        Assert.Equal(QueueState.Resolved, resolved.State);
        var history = File.ReadAllText(resolved.FullPath);
        Assert.Contains("### Blocked", history);
        Assert.Contains("### Reopened", history);
        Assert.Contains("### Resolved\n\nCleared and documented", history);

        var journalPath = queue.Layout.ResolutionJournalPath;
        Assert.True(File.Exists(journalPath));
        var journal = File.ReadAllText(journalPath);
        Assert.Contains(ResolutionJournal.HeaderMarker, journal);
        Assert.Contains(ResolutionJournal.EntryMarker(added.Id, "Cleared and documented the cache."), journal);
        Assert.Contains("### Problem\n\nThe cache is stale.", journal);
        Assert.Contains("### Resolution\n\nCleared and documented the cache.", journal);
        Assert.Contains(queue.Layout.RelativePath(QueueState.Resolved, added.Id), journal);
        Assert.False(File.Exists(Path.Combine(directory.Path, "AGENTS.md")));

        Assert.Equal(0, queue.List(includeResolved: false).Count);
        Assert.Equal(1, queue.List(includeResolved: true).Count);
    }

    private static void ConcurrentLifecycleTransitionsHaveOneWinner()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var setupQueue = new PaperqQueue(directory.Path);
        var record = setupQueue.Add("one transition wins");
        setupQueue.Next(claim: true);

        var results = new ConcurrentBag<string>();
        var resolve = Task.Run(() => RunTransition(
            results,
            () => new PaperqQueue(directory.Path).Resolve(record.Id, "resolved evidence"),
            "resolved"));
        var block = Task.Run(() => RunTransition(
            results,
            () => new PaperqQueue(directory.Path).Block(record.Id, "blocked evidence"),
            "blocked"));
        Task.WaitAll(resolve, block);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results.Count(result => result.StartsWith("success:", StringComparison.Ordinal)));
        Assert.Equal(1, results.Count(result => result.StartsWith("conflict:", StringComparison.Ordinal)));

        var queue = new PaperqQueue(directory.Path);
        var current = queue.List(includeResolved: true).Single();
        Assert.True(current.State is QueueState.Resolved or QueueState.Blocked);
        Assert.Equal(0, Directory.GetFiles(queue.Layout.StateDirectory(QueueState.Working), "*.md").Length);
        if (current.State == QueueState.Resolved)
        {
            var journal = File.ReadAllText(queue.Layout.ResolutionJournalPath);
            Assert.Equal(1, CountOccurrences(journal, ResolutionJournal.EntryMarker(record.Id, "resolved evidence")));
        }
        else
        {
            Assert.False(File.Exists(queue.Layout.ResolutionJournalPath));
        }
    }

    private static void ResolutionJournalRetriesAreIdempotent()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var record = queue.Add("SSH needs the project identity and proxy jump options.");
        queue.Next(claim: true);

        var first = queue.Resolve(record.Id, "Added a project SSH wrapper and documented its use.");
        var retry = queue.Resolve(record.Id, "Added a project SSH wrapper and documented its use.");
        Assert.Equal(first, retry);

        var journal = File.ReadAllText(queue.Layout.ResolutionJournalPath);
        Assert.Equal(
            1,
            CountOccurrences(
                journal,
                ResolutionJournal.EntryMarker(record.Id, "Added a project SSH wrapper and documented its use.")));
        Assert.Equal(1, CountOccurrences(journal, "Added a project SSH wrapper and documented its use."));

        var differentNote = Assert.Throws<PaperqException>(() =>
            queue.Resolve(record.Id, "A different resolution must not overwrite the first."));
        Assert.Equal("invalid_transition", differentNote.Code);

        queue.Reopen(record.Id);
        queue.Next(claim: true);
        queue.Resolve(record.Id, "Updated the wrapper after the SSH proxy changed.");
        journal = File.ReadAllText(queue.Layout.ResolutionJournalPath);
        Assert.Equal(2, CountOccurrences(journal, ResolutionJournal.EntryMarkerPrefix));
        Assert.Equal(
            1,
            CountOccurrences(
                journal,
                ResolutionJournal.EntryMarker(record.Id, "Updated the wrapper after the SSH proxy changed.")));
    }

    private static void ConcurrentResolutionsShareOneJournal()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var setupQueue = new PaperqQueue(directory.Path);
        const int count = 12;
        var records = new List<PapercutRecord>();
        for (var index = 0; index < count; index++)
        {
            var added = setupQueue.Add($"concurrent resolution {index}");
            records.Add(added);
            setupQueue.Next(claim: true);
        }

        var tasks = records
            .Select((record, index) => Task.Run(() =>
                new PaperqQueue(directory.Path).Resolve(record.Id, $"verified solution {index}")))
            .ToArray();
        Task.WaitAll(tasks);

        var journal = File.ReadAllText(setupQueue.Layout.ResolutionJournalPath);
        Assert.Equal(count, CountOccurrences(journal, ResolutionJournal.EntryMarkerPrefix));
        for (var index = 0; index < records.Count; index++)
        {
            Assert.Equal(
                1,
                CountOccurrences(
                    journal,
                    ResolutionJournal.EntryMarker(records[index].Id, $"verified solution {index}")));
        }
    }

    private static void ExistingResolutionDocumentIsPreserved()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var record = queue.Add("do not overwrite an unrelated document");
        queue.Next(claim: true);
        var journalPath = queue.Layout.ResolutionJournalPath;
        const string existing = "# My unrelated resolutions\n";
        File.WriteAllText(journalPath, existing);

        var exception = Assert.Throws<PaperqException>(() =>
            queue.Resolve(record.Id, "this should not be written"));
        Assert.Equal("invalid_resolution_journal", exception.Code);
        Assert.Equal(existing, File.ReadAllText(journalPath));
        Assert.True(File.Exists(queue.Layout.RecordPath(QueueState.Working, record.Id)));
    }

    private static void ResolveJsonIdentifiesJournal()
    {
        using var directory = new TestDirectory();
        Assert.Equal(0, RunCli(directory.Path, ["init"]).ExitCode);
        var queue = new PaperqQueue(directory.Path);
        var record = queue.Add("JSON resolution path");
        queue.Next(claim: true);

        var result = RunCli(directory.Path, ["resolve", record.Id, "--note", "Recorded in the journal.", "--json"]);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(QueueLayout.ResolutionJournalFileName, data.GetProperty("resolutionPath").GetString());
        Assert.True(File.Exists(Path.Combine(directory.Path, data.GetProperty("resolutionPath").GetString()!)));
    }

    private static void DuplicateStatesAreRejected()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var record = queue.Add("duplicate me");
        File.Copy(record.FullPath, queue.Layout.RecordPath(QueueState.Working, record.Id));

        var exception = Assert.Throws<PaperqException>(() => queue.List(includeResolved: true));
        Assert.Equal("duplicate_record", exception.Code);
        var showException = Assert.Throws<PaperqException>(() => queue.Show(record.Id));
        Assert.Equal("duplicate_record", showException.Code);
    }

    private static void JsonErrorsStayOnStdout()
    {
        using var directory = new TestDirectory();
        Assert.Equal(0, RunCli(directory.Path, ["init"]).ExitCode);

        var result = RunCli(directory.Path, ["next", "--claim", "--json"]);
        Assert.Equal((int)PaperqExitCode.NotFound, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        using var document = JsonDocument.Parse(result.Output);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("queue_empty", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static CliResult RunCli(
        string root,
        IReadOnlyList<string> arguments,
        string input = "",
        bool interactive = false)
    {
        var commandArguments = arguments.Concat(["--root", root]).ToArray();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        using var reader = new StringReader(input);
        var exitCode = CliApplication.Run(commandArguments, new CliIo(reader, output, error, interactive));
        return new CliResult(exitCode, output.ToString().TrimEnd(), error.ToString().TrimEnd());
    }

    private static void RunTransition(
        ConcurrentBag<string> results,
        Func<PapercutRecord> transition,
        string expectedState)
    {
        try
        {
            var record = transition();
            Assert.Equal(expectedState, record.State.ToDirectoryName());
            results.Add($"success:{expectedState}");
        }
        catch (PaperqException exception) when (exception.ExitCode == PaperqExitCode.Conflict)
        {
            results.Add($"conflict:{exception.Code}");
        }
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private readonly Queue<DateTimeOffset> _values = new(values);

        public override DateTimeOffset GetUtcNow() => _values.Dequeue();
    }
}

internal static class Assert
{
    internal static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected true, got false.");
        }
    }

    internal static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false, got true.");

    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}>, got <{actual}>.");
        }
    }

    internal static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to contain <{expectedSubstring}>. Actual: <{actual}>");
        }
    }

    internal static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }
}

internal sealed class TestDirectory : IDisposable
{
    private static readonly string TestBase = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "paperq-tests");

    internal TestDirectory(bool git = false)
    {
        Path = System.IO.Path.Combine(TestBase, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path);
        if (git)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
        }
    }

    internal string Path { get; }

    public void Dispose()
    {
        var target = System.IO.Path.GetFullPath(Path);
        var safeBase = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(TestBase)) +
                       System.IO.Path.DirectorySeparatorChar;
        if (!target.StartsWith(safeBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete unexpected test path: {target}");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
