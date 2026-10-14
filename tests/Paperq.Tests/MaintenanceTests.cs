using System.IO.Compression;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Paperq.Tests;

internal static partial class TestSuite
{
    private static PapercutRecord AddResolved(PaperqQueue queue, string message, string note)
    {
        var record = queue.Add(message);
        queue.Claim(record.Id);
        return queue.Resolve(record.Id, note);
    }

    private static void PruneDeletesSelectedRecords()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var open = queue.Add("obsolete open item");
        var resolved = AddResolved(queue, "obsolete problem", "obsolete remedy");
        var keep = AddResolved(queue, "keep problem", "keep remedy");
        var before = File.ReadAllBytes(queue.Layout.ResolutionJournalPath);
        var args = new[] { "prune", open.Id, resolved.Id, "--delete", "--reason", "outdated", "--json" };
        var preview = RunCli(directory.Path, args);
        Assert.Equal(0, preview.ExitCode);
        using (var document = JsonDocument.Parse(preview.Output))
        {
            Assert.False(document.RootElement.GetProperty("data").GetProperty("applied").GetBoolean());
        }
        Assert.True(before.SequenceEqual(File.ReadAllBytes(queue.Layout.ResolutionJournalPath)));
        Assert.True(File.Exists(open.FullPath));
        Assert.True(File.Exists(resolved.FullPath));
        Assert.False(Directory.Exists(Path.Combine(queue.Layout.QueueRoot, "archives")));

        Assert.Equal(0, RunCli(directory.Path, args.Concat(["--apply"]).ToArray()).ExitCode);
        Assert.False(File.Exists(open.FullPath));
        Assert.False(File.Exists(resolved.FullPath));
        var journal = File.ReadAllText(queue.Layout.ResolutionJournalPath);
        Assert.False(journal.Contains(resolved.Id, StringComparison.Ordinal));
        Assert.False(journal.Contains("obsolete", StringComparison.Ordinal));
        Assert.Contains("keep remedy", journal);
        Assert.True(File.Exists(keep.FullPath));
        Assert.Equal("not_found", Assert.Throws<PaperqException>(() => queue.Resolve(resolved.Id, "obsolete remedy")).Code);
        Assert.False(Directory.Exists(Path.Combine(queue.Layout.QueueRoot, ".maintenance")));
        Assert.False(Directory.Exists(Path.Combine(queue.Layout.QueueRoot, "archives")));
        Assert.Equal(0, RunCli(directory.Path, ["prune", keep.Id, "--delete", "--reason", "retired", "--apply"]).ExitCode);
        Assert.False(File.Exists(queue.Layout.ResolutionJournalPath));
    }

    private static void PruneArchivesSelectedRecords()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var open = queue.Add(new string('x', 5000));
        var resolved = AddResolved(queue, "obsolete problem", new string('y', 5000));
        // Archive exact source bytes, including a supported UTF-8 BOM and CRLF.
        File.WriteAllText(open.FullPath, File.ReadAllText(open.FullPath).Replace("\n", "\r\n", StringComparison.Ordinal), new System.Text.UTF8Encoding(true));
        var original = new Dictionary<string, byte[]>
        {
            [queue.Layout.RelativePath(open.State, open.Id)] = File.ReadAllBytes(open.FullPath),
            [queue.Layout.RelativePath(resolved.State, resolved.Id)] = File.ReadAllBytes(resolved.FullPath),
        };
        var result = RunCli(directory.Path, ["prune", open.Id, resolved.Id, "--archive", "--reason", "superseded", "--apply", "--json"]);
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var relative = document.RootElement.GetProperty("data").GetProperty("archivePath").GetString()!;
        var path = Path.Combine(directory.Path, relative);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length < original.Values.Sum(bytes => bytes.Length));
        using var zip = ZipFile.OpenRead(path);
        foreach (var (name, bytes) in original)
        {
            using var source = zip.GetEntry(name)!.Open();
            using var output = new MemoryStream();
            source.CopyTo(output);
            Assert.True(bytes.SequenceEqual(output.ToArray()));
        }
        using (var reader = new StreamReader(zip.GetEntry("PAPERQ_RESOLUTIONS.md")!.Open()))
        {
            Assert.Contains(resolved.Id, reader.ReadToEnd());
        }
        using (var reader = new StreamReader(zip.GetEntry("prune.txt")!.Open()))
        {
            Assert.Contains("superseded", reader.ReadToEnd());
        }
        Assert.False(File.Exists(open.FullPath));
        Assert.False(File.Exists(resolved.FullPath));
        Assert.False(File.Exists(queue.Layout.ResolutionJournalPath));
        Assert.Equal("not_found", Assert.Throws<PaperqException>(() => queue.Resolve(resolved.Id, new string('y', 5000))).Code);
        Assert.Equal(0, queue.List(includeResolved: true).Count);
    }

    private static void MaintenanceValidatesSelections()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var open = queue.Add("keep open");
        var working = queue.Add("keep working");
        queue.Claim(working.Id);
        Assert.Equal(5, RunCli(directory.Path, ["prune", open.Id, working.Id, "--delete", "--reason", "retired", "--apply"]).ExitCode);
        Assert.True(File.Exists(open.FullPath));
        queue.Block(working.Id, "keep blocked");
        Assert.Equal(5, RunCli(directory.Path, ["prune", working.Id, "--delete", "--reason", "retired", "--apply"]).ExitCode);
        Assert.Equal(6, RunCli(directory.Path, ["prune", open.Id, "../bad", "--delete", "--reason", "retired", "--apply"]).ExitCode);
        Assert.True(File.Exists(open.FullPath));
        Assert.Equal(5, RunCli(directory.Path, ["consolidate", open.Id, working.Id, "--note", "summary", "--apply"]).ExitCode);
        Assert.False(Directory.Exists(Path.Combine(queue.Layout.QueueRoot, "archives")));
    }

    private static void ConsolidationPreservesHistory()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var first = AddResolved(queue, "first repeated problem", "first detailed remedy");
        var second = AddResolved(queue, "second repeated problem", "second detailed remedy");
        var keep = AddResolved(queue, "keep problem", "keep remedy");
        var before = File.ReadAllBytes(queue.Layout.ResolutionJournalPath);
        var firstBytes = File.ReadAllBytes(first.FullPath);
        var secondBytes = File.ReadAllBytes(second.FullPath);
        var args = new[] { "consolidate", first.Id, second.Id, "--note", "shared solution", "--json" };
        Assert.Equal(0, RunCli(directory.Path, args).ExitCode);
        Assert.True(before.SequenceEqual(File.ReadAllBytes(queue.Layout.ResolutionJournalPath)));
        Assert.Equal(0, RunCli(directory.Path, args.Concat(["--apply"]).ToArray()).ExitCode);
        var compact = File.ReadAllText(queue.Layout.ResolutionJournalPath);
        Assert.Contains("shared solution", compact);
        Assert.Contains("keep remedy", compact);
        Assert.False(compact.Contains("first detailed remedy", StringComparison.Ordinal));
        Assert.False(compact.Contains("second repeated problem", StringComparison.Ordinal));
        Assert.Contains(ResolutionJournal.EntryMarker(first.Id, "first detailed remedy"), compact);
        Assert.Contains(ResolutionJournal.EntryMarker(second.Id, "second detailed remedy"), compact);
        Assert.True(firstBytes.SequenceEqual(File.ReadAllBytes(first.FullPath)));
        Assert.True(secondBytes.SequenceEqual(File.ReadAllBytes(second.FullPath)));
        queue.Resolve(first.Id, "first detailed remedy");
        Assert.Equal(compact, File.ReadAllText(queue.Layout.ResolutionJournalPath));
        Assert.Equal(0, RunCli(directory.Path, args.Concat(["--apply"]).ToArray()).ExitCode);
        Assert.Equal(compact, File.ReadAllText(queue.Layout.ResolutionJournalPath));
        queue.Reopen(first.Id);
        queue.Claim(first.Id);
        queue.Resolve(first.Id, "new verified remedy");
        Assert.Contains("new verified remedy", File.ReadAllText(queue.Layout.ResolutionJournalPath));
        Assert.True(File.Exists(keep.FullPath));
    }

    private static void PruneUpdatesConsolidatedGroups()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var first = AddResolved(queue, "first", "first note");
        var second = AddResolved(queue, "second", "second note");
        Assert.Equal(0, RunCli(directory.Path, ["consolidate", first.Id, second.Id, "--note", "shared fix", "--apply"]).ExitCode);
        Assert.Equal(0, RunCli(directory.Path, ["prune", first.Id, "--delete", "--reason", "obsolete", "--apply"]).ExitCode);
        var journal = File.ReadAllText(queue.Layout.ResolutionJournalPath);
        Assert.False(journal.Contains(first.Id, StringComparison.Ordinal));
        Assert.Contains(second.Id, journal);
        Assert.Contains("shared fix", journal);
        queue.Resolve(second.Id, "second note");
        Assert.Equal(journal, File.ReadAllText(queue.Layout.ResolutionJournalPath));
        Assert.Equal(0, RunCli(directory.Path, ["prune", second.Id, "--delete", "--reason", "obsolete", "--apply"]).ExitCode);
        Assert.False(File.Exists(queue.Layout.ResolutionJournalPath));
    }

    private static void MaintenancePreservesJournalText()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var first = AddResolved(queue, "first", "first note");
        var second = AddResolved(queue, "second", "second note");
        var path = queue.Layout.ResolutionJournalPath;
        File.WriteAllText(path, "Custom introduction\n\n" + File.ReadAllText(path));
        Assert.Equal(0, RunCli(directory.Path, ["consolidate", first.Id, second.Id, "--note", "summary", "--apply"]).ExitCode);
        Assert.True(File.ReadAllText(path).StartsWith("Custom introduction\n\n", StringComparison.Ordinal));
        var corrupt = File.ReadAllText(path) + "\n<!-- paperq:resolution:bad -->\n";
        File.WriteAllText(path, corrupt);
        Assert.Equal(6, RunCli(directory.Path, ["prune", first.Id, "--delete", "--reason", "old", "--apply"]).ExitCode);
        Assert.Equal(corrupt, File.ReadAllText(path));
        Assert.True(File.Exists(first.FullPath));
    }

    private static void MaintenanceCliOptions()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var first = AddResolved(queue, "first", "note one");
        var second = AddResolved(queue, "second", "note two");
        Assert.Equal(0, RunCli(directory.Path, ["consolidate", first.Id, second.Id, "--stdin", "--apply"], "summary\nwith evidence").ExitCode);
        Assert.Contains("summary\nwith evidence", File.ReadAllText(queue.Layout.ResolutionJournalPath));
        foreach (var args in new[]
        {
            new[] { "prune", first.Id, "--reason", "obsolete", "--apply" },
            new[] { "prune", first.Id, "--delete", "--archive", "--reason", "obsolete", "--apply" },
            new[] { "prune", first.Id, "--delete", "--reason", "obsolete", "--stdin", "--apply" },
            new[] { "consolidate", first.Id, "--note", "summary" },
            new[] { "consolidate", first.Id, first.Id, "--note", "summary" },
        })
        {
            Assert.Equal(2, RunCli(directory.Path, args).ExitCode);
        }
        Assert.Equal(0, RunCli(directory.Path, ["prune", "--help", "--json"]).ExitCode);
        Assert.Equal(0, RunCli(directory.Path, ["help", "consolidate", "--json"]).ExitCode);
        Assert.Equal(0, RunCli(directory.Path, ["prune", first.Id, "--delete", "--stdin", "--apply"], "obsolete").ExitCode);
    }

    private static void InterruptedPruneRecovers()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Windows file sharing supplies a deterministic failure after the
            // first deletion. The process-independent recovery fixture below
            // still checks recovery on every supported operating system.
            InterruptedPruneFixtureRecovers();
            return;
        }

        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var first = AddResolved(queue, "first", "first note");
        var second = AddResolved(queue, "second", "second note");
        using (var held = new FileStream(second.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = RunCli(directory.Path, ["prune", first.Id, second.Id, "--delete", "--reason", "obsolete", "--apply"]);
            Assert.Equal(1, result.ExitCode);
            Assert.False(File.Exists(first.FullPath));
            Assert.True(File.Exists(second.FullPath));
            Assert.False(File.Exists(queue.Layout.ResolutionJournalPath));
        }

        var preview = RunCli(directory.Path, ["prune", second.Id, "--delete", "--reason", "obsolete", "--json"]);
        Assert.Equal(5, preview.ExitCode);
        Assert.Contains("maintenance_pending", preview.Output);
        Assert.True(File.Exists(second.FullPath));
        queue.Add("new work after recovery");
        Assert.False(File.Exists(second.FullPath));
        Assert.False(Directory.Exists(Path.Combine(queue.Layout.QueueRoot, ".maintenance")));
        InterruptedPruneFixtureRecovers();
    }

    private static void InterruptedPruneFixtureRecovers()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var record = AddResolved(queue, "obsolete", "old note");
        var before = File.ReadAllText(queue.Layout.ResolutionJournalPath);
        var transaction = Path.Combine(queue.Layout.QueueRoot, ".maintenance");
        Directory.CreateDirectory(transaction);
        static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
        File.WriteAllText(Path.Combine(transaction, "journal.next"), "");
        File.WriteAllText(Path.Combine(transaction, "prune.pending"),
            $"paperq-prune-v1\n{Hash(before)}\n{Hash("")}\n{record.Id}\tresolved\t{Hash(File.ReadAllText(record.FullPath))}\n");
        queue.Add("resume");
        Assert.False(File.Exists(record.FullPath));
        Assert.False(File.Exists(queue.Layout.ResolutionJournalPath));
        Assert.False(Directory.Exists(transaction));

        Directory.CreateDirectory(transaction);
        File.WriteAllText(Path.Combine(transaction, "journal.next"), "incomplete prepare");
        File.WriteAllText(Path.Combine(transaction, "prune.prepare"), "partial plan");
        File.WriteAllText(Path.Combine(transaction, "archive.next"), "partial ZIP");
        File.WriteAllText(Path.Combine(transaction, "archive.info"), "partial metadata");
        queue.Add("clean incomplete prepare");
        Assert.False(Directory.Exists(transaction));
        Assert.Equal(2, queue.List(includeResolved: true).Count);
    }

    private static void ArchiveFailuresPreserveQueue()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var record = AddResolved(queue, "keep", "keep note");
        var before = File.ReadAllBytes(queue.Layout.ResolutionJournalPath);
        var conflict = Path.Combine(queue.Layout.QueueRoot, "archives");
        File.WriteAllText(conflict, "unrelated file");
        Assert.Equal(6, RunCli(directory.Path, ["prune", record.Id, "--archive", "--reason", "obsolete", "--apply"]).ExitCode);
        Assert.True(File.Exists(record.FullPath));
        Assert.True(before.SequenceEqual(File.ReadAllBytes(queue.Layout.ResolutionJournalPath)));
        Assert.Equal("unrelated file", File.ReadAllText(conflict));
        Assert.False(Directory.Exists(Path.Combine(queue.Layout.QueueRoot, ".maintenance")));
    }

    private static void ArchivePreparationFailuresLeaveNoZip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var record = AddResolved(queue, "keep original", "keep original note");
        var before = File.ReadAllBytes(queue.Layout.ResolutionJournalPath);
        var archives = Path.Combine(queue.Layout.QueueRoot, "archives");
        Directory.CreateDirectory(archives);
        var root = new DirectoryInfo(queue.Layout.QueueRoot);
        var original = root.GetAccessControl();
        var denied = root.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateDirectories,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            root.SetAccessControl(denied);
            var result = RunCli(directory.Path, ["prune", record.Id, "--archive", "--reason", "obsolete", "--apply"]);
            Assert.Equal(1, result.ExitCode);
            Assert.True(File.Exists(record.FullPath));
            Assert.True(before.SequenceEqual(File.ReadAllBytes(queue.Layout.ResolutionJournalPath)));
            Assert.Equal(0, Directory.GetFiles(archives).Length);
        }
        finally
        {
            original.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            root.SetAccessControl(original);
        }
    }

    private static void PreparedArchivesRecover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var record = AddResolved(queue, "keep until archived", "verified note");
        var bytes = File.ReadAllBytes(record.FullPath);
        var before = File.ReadAllBytes(queue.Layout.ResolutionJournalPath);
        var archives = new DirectoryInfo(Path.Combine(queue.Layout.QueueRoot, "archives"));
        archives.Create();
        var original = archives.GetAccessControl();
        var denied = archives.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.CreateFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            archives.SetAccessControl(denied);
            Assert.Equal(1, RunCli(directory.Path, ["prune", record.Id, "--archive", "--reason", "retired", "--apply"]).ExitCode);
            Assert.True(File.Exists(record.FullPath));
            Assert.True(before.SequenceEqual(File.ReadAllBytes(queue.Layout.ResolutionJournalPath)));
            Assert.Equal(0, archives.GetFiles().Length);
            Assert.True(File.Exists(Path.Combine(queue.Layout.QueueRoot, ".maintenance", "prune.pending")));
        }
        finally
        {
            original.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            archives.SetAccessControl(original);
        }

        queue.Add("resume after restoring access");
        Assert.False(File.Exists(record.FullPath));
        Assert.False(Directory.Exists(Path.Combine(queue.Layout.QueueRoot, ".maintenance")));
        using var zip = ZipFile.OpenRead(archives.GetFiles("*.zip").Single().FullName);
        using var source = zip.GetEntry(queue.Layout.RelativePath(QueueState.Resolved, record.Id))!.Open();
        using var recovered = new MemoryStream();
        source.CopyTo(recovered);
        Assert.True(bytes.SequenceEqual(recovered.ToArray()));
    }

    private static void MaintenancePreservesFormatting()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var first = AddResolved(queue, "first", "first note");
        var second = AddResolved(queue, "second", "second note");
        var third = AddResolved(queue, "unrelated", "unrelated note");
        var path = queue.Layout.ResolutionJournalPath;
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n", StringComparison.Ordinal));
        var original = File.ReadAllText(path);
        var start = original.IndexOf(ResolutionJournal.EntryMarker(third.Id, "unrelated note"), StringComparison.Ordinal);
        var untouched = original[start..];
        Assert.Equal(0, RunCli(directory.Path, ["consolidate", first.Id, second.Id, "--note", "summary", "--apply"]).ExitCode);
        Assert.True(File.ReadAllText(path).EndsWith(untouched, StringComparison.Ordinal));
        // Explicit group boundaries allow unrelated hand-written text after it.
        var groupEnd = File.ReadAllText(path).IndexOf("<!-- paperq:consolidated:end -->", StringComparison.Ordinal)
                       + "<!-- paperq:consolidated:end -->".Length;
        File.WriteAllText(path, File.ReadAllText(path).Insert(groupEnd, "\n\nCustom trailing text\n\n"));
        Assert.Equal(0, RunCli(directory.Path, ["prune", first.Id, second.Id, "--delete", "--reason", "obsolete", "--apply"]).ExitCode);
        Assert.Contains("Custom trailing text", File.ReadAllText(path));
        Assert.True(File.ReadAllText(path).EndsWith(untouched, StringComparison.Ordinal));
    }

    private static void MaintenanceExecutableProcess()
    {
        using var directory = new TestDirectory();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        foreach (var argument in new[] { "init", "--root", directory.Path, "--json" })
        {
            start.ArgumentList.Add(argument);
        }
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", error);
            using var json = JsonDocument.Parse(output);
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        }
        var queue = new PaperqQueue(directory.Path);
        var first = AddResolved(queue, "process first", "first note");
        var second = AddResolved(queue, "process second", "second note");
        start.ArgumentList.Clear();
        start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        foreach (var argument in new[] { "consolidate", first.Id, second.Id, "--note", "process summary", "--apply", "--root", directory.Path, "--json" })
        {
            start.ArgumentList.Add(argument);
        }
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", error);
            using var json = JsonDocument.Parse(output);
            Assert.True(json.RootElement.GetProperty("data").GetProperty("applied").GetBoolean());
        }
        Assert.Contains("process summary", File.ReadAllText(queue.Layout.ResolutionJournalPath));
        start.ArgumentList.Clear();
        start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        foreach (var argument in new[] { "prune", first.Id, second.Id, "--archive", "--reason", "process test", "--apply", "--root", directory.Path, "--json" })
        {
            start.ArgumentList.Add(argument);
        }
        using (var process = Process.Start(start)!)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", error);
            using var json = JsonDocument.Parse(output);
            var archive = json.RootElement.GetProperty("data").GetProperty("archivePath").GetString()!;
            using var zip = ZipFile.OpenRead(Path.Combine(directory.Path, archive));
            Assert.True(zip.GetEntry(queue.Layout.RelativePath(QueueState.Resolved, first.Id)) is not null);
            Assert.True(zip.GetEntry(queue.Layout.RelativePath(QueueState.Resolved, second.Id)) is not null);
        }
        Assert.False(File.Exists(first.FullPath));
        Assert.False(File.Exists(second.FullPath));
        Assert.False(File.Exists(queue.Layout.ResolutionJournalPath));
    }

    private static void ConcurrentMaintenanceAndResolutions()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var first = AddResolved(queue, "first", "first note");
        var second = AddResolved(queue, "second", "second note");
        var pending = queue.Add("pending");
        queue.Claim(pending.Id);
        var compact = Task.Run(() => RunCli(directory.Path, ["consolidate", first.Id, second.Id, "--note", "summary", "--apply"]));
        var append = Task.Run(() => RunCli(directory.Path, ["resolve", pending.Id, "--note", "pending remedy"]));
        Task.WaitAll(compact, append);
        Assert.Equal(0, compact.Result.ExitCode);
        Assert.Equal(0, append.Result.ExitCode);
        var journal = File.ReadAllText(queue.Layout.ResolutionJournalPath);
        Assert.Contains("summary", journal);
        Assert.Contains("pending remedy", journal);
        var prune = Task.Run(() => RunCli(directory.Path, ["prune", first.Id, "--delete", "--reason", "obsolete", "--apply"]));
        var retry = Task.Run(() => RunCli(directory.Path, ["resolve", first.Id, "--note", "first note"]));
        Task.WaitAll(prune, retry);
        Assert.Equal(0, prune.Result.ExitCode);
        Assert.True(retry.Result.ExitCode is 0 or 4);
        Assert.False(File.ReadAllText(queue.Layout.ResolutionJournalPath).Contains(first.Id, StringComparison.Ordinal));
        Assert.False(File.Exists(first.FullPath));
    }
}
