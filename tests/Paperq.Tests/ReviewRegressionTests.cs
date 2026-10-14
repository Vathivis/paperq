using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Paperq.Tests;

internal static partial class TestSuite
{
    private static void LegacyRecordMarkersRemainReadable()
    {
        using var directory = new TestDirectory();
        new QueueLayout(directory.Path).Create();
        var queue = new PaperqQueue(directory.Path);
        var created = DateTimeOffset.UtcNow;
        var id = PapercutId.Create(created);
        const string message = "The tool prints <!-- paperq:example -->.";
        File.WriteAllText(queue.Layout.RecordPath(QueueState.Open, id), RecordCodec.FormatNew(id, created, message));

        var shown = RunCli(directory.Path, ["show", id, "--json"]);
        Assert.Equal(0, shown.ExitCode);
        using var json = JsonDocument.Parse(shown.Output);
        Assert.Equal(message, json.RootElement.GetProperty("data").GetProperty("message").GetString());
        Assert.Equal(0, RunCli(directory.Path, ["list", "--json"]).ExitCode);
        Assert.Equal(0, RunCli(directory.Path, ["claim", id]).ExitCode);
        Assert.Equal(0, RunCli(directory.Path, ["block", id, "--reason", "needs access"]).ExitCode);
        Assert.Equal(0, RunCli(directory.Path, ["reopen", id]).ExitCode);
        Assert.Equal(0, RunCli(directory.Path, ["prune", id, "--delete", "--reason", "retired", "--apply"]).ExitCode);
        Assert.Equal((int)PaperqExitCode.InvalidData, RunCli(directory.Path, ["add", message]).ExitCode);
    }

    private static void PruningLargeHistoriesUsesBoundedMemory()
    {
        // All individual notes and records stay within their documented limits.
        // A legacy resolved queue may have no resolution journal to backfill.
        var cycle = RecordCodec.FormatEvent("Blocked", new string('x', InputRules.MaxInputBytes - 1))
                    + RecordCodec.FormatEvent("Reopened", null);
        var history = string.Concat(Enumerable.Repeat(cycle, 14)) + RecordCodec.FormatEvent("Resolved", "retired fix");
        foreach (var mode in new[] { "--delete", "--archive" })
        {
            using var directory = new TestDirectory();
            var layout = new QueueLayout(directory.Path);
            layout.Create();
            var ids = new List<string>();
            for (var index = 0; index < 32; index++)
            {
                var created = DateTimeOffset.UtcNow;
                var id = PapercutId.Create(created);
                var content = RecordCodec.FormatNew(id, created, "obsolete problem") + history;
                Assert.True(Encoding.UTF8.GetByteCount(content) < InputRules.MaxRecordBytes);
                File.WriteAllText(layout.RecordPath(QueueState.Resolved, id), content);
                ids.Add(id);
            }

            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.Environment["DOTNET_GCHeapHardLimit"] = "0x4000000"; // 64 MiB
            start.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
            foreach (var argument in new[] { "prune" }.Concat(ids)
                         .Concat([mode, "--reason", "outdated", "--apply", "--json", "--root", directory.Path]))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"{mode} exited {process.ExitCode}: {output} {error}");
            using var json = JsonDocument.Parse(output);
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(32, json.RootElement.GetProperty("data").GetProperty("count").GetInt32());
            Assert.Equal(0, Directory.GetFiles(layout.StateDirectory(QueueState.Resolved), "*.md").Length);
        }
    }
}
