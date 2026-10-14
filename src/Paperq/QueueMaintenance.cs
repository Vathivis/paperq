using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Paperq;

internal sealed record MaintenanceResult(
    IReadOnlyList<string> Ids, bool Applied, string? Mode, string? ArchivePath,
    int BeforeBytes, int AfterBytes, string Text);

internal sealed record PruneRecord(string Id, QueueState State, string FullPath);

internal static class QueueMaintenance
{
    private const string TransactionDirectory = ".maintenance";
    private const string PlanFile = "prune.pending";
    private const string JournalFile = "journal.next";
    private const string PreparedPlanFile = "prune.prepare";
    private const string ArchiveFile = "archive.next";
    private const string ArchiveInfoFile = "archive.info";

    internal static MaintenanceResult Consolidate(PaperqQueue queue, IReadOnlyList<string> ids, string rawNote, bool apply)
    {
        using var mutation = QueueMutation.Enter(queue.Layout, recover: apply);
        var note = InputRules.Validate(rawNote, "note");
        ValidateIds(ids);
        foreach (var id in ids)
        {
            if (queue.Show(id).State != QueueState.Resolved)
            {
                throw InvalidState("Consolidation accepts only resolved records.");
            }
        }

        using var journalLock = new ResolutionJournal(queue.Layout).AcquireLock();
        var before = ReadJournal(queue.Layout);
        var document = JournalDocument.Parse(before);
        var after = document.Consolidate(ids.ToHashSet(StringComparer.Ordinal), note);
        CheckJournalSize(after);
        if (apply && before != after)
        {
            TextFile.Replace(queue.Layout.ResolutionJournalPath, after, ResolutionJournal.MaxJournalBytes);
        }

        return Result(ids, apply, null, null, before, after, note);
    }

    internal static MaintenanceResult Prune(PaperqQueue queue, IReadOnlyList<string> ids, string rawReason, bool archive, bool apply)
    {
        using var mutation = QueueMutation.Enter(queue.Layout, recover: apply);
        var reason = InputRules.Validate(rawReason, "reason");
        ValidateIds(ids);
        var records = new List<PruneRecord>();
        foreach (var id in ids)
        {
            var record = queue.Show(id);
            if (record.State is not (QueueState.Open or QueueState.Resolved))
            {
                throw InvalidState("Pruning accepts only open or resolved records.");
            }

            records.Add(new PruneRecord(record.Id, record.State, record.FullPath));
        }

        using var journalLock = new ResolutionJournal(queue.Layout).AcquireLock();
        var before = ReadJournal(queue.Layout);
        var (after, removed) = JournalDocument.Parse(before).Prune(ids.ToHashSet(StringComparer.Ordinal));
        CheckJournalSize(after);
        string? archivePath = null;
        if (apply)
        {
            var plan = new StringBuilder($"paperq-prune-v1\n{Hash(before)}\n{Hash(after)}\n");
            var directory = Path.Combine(queue.Layout.QueueRoot, TransactionDirectory);
            EnsureDirectory(directory);
            try
            {
                if (archive)
                {
                    archivePath = WriteArchive(queue.Layout, records, removed, reason, plan, directory);
                }
                else
                {
                    foreach (var record in records)
                    {
                        CaptureRecord(record, plan);
                    }
                }

                TextFile.CreateNew(Path.Combine(directory, JournalFile), after);
                // Publish the plan last. After this point recovery completes this operation.
                TextFile.CreateNew(Path.Combine(directory, PreparedPlanFile), plan.ToString());
                File.Move(Path.Combine(directory, PreparedPlanFile), Path.Combine(directory, PlanFile), overwrite: false);
                CompletePrune(queue.Layout, directory);
            }
            catch
            {
                if (!File.Exists(Path.Combine(directory, PlanFile)))
                {
                    CleanStaging(directory);
                }

                throw;
            }
        }

        return Result(ids, apply, archive ? "archive" : "delete", archivePath, before, after, reason);
    }

    internal static void Recover(QueueLayout layout)
    {
        var directory = Path.Combine(layout.QueueRoot, TransactionDirectory);
        if (!Directory.Exists(directory))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null || File.Exists(directory))
            {
                throw InvalidPending();
            }

            return;
        }

        EnsureDirectory(directory);
        var knownFiles = new[] { PlanFile, JournalFile, PreparedPlanFile, ArchiveFile, ArchiveInfoFile };
        if (Directory.EnumerateFileSystemEntries(directory).Any(path => !knownFiles.Contains(Path.GetFileName(path), StringComparer.Ordinal)))
        {
            throw InvalidPending();
        }

        using var journalLock = new ResolutionJournal(layout).AcquireLock();
        if (File.Exists(Path.Combine(directory, PlanFile)))
        {
            CompletePrune(layout, directory);
        }
        else
        {
            // A prepare interrupted before publishing the plan made no queue changes.
            CleanStaging(directory);
        }
    }

    internal static void RequireNoPending(QueueLayout layout)
    {
        var path = Path.Combine(layout.QueueRoot, TransactionDirectory);
        if (Directory.Exists(path) || File.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
        {
            throw new PaperqException("maintenance_pending",
                "An interrupted operation needs recovery. A preview cannot recover it; run an applied maintenance or queue mutation command.",
                PaperqExitCode.Conflict);
        }
    }

    private static void CompletePrune(QueueLayout layout, string directory)
    {
        var lines = TextFile.ReadManaged(Path.Combine(directory, PlanFile), InputRules.MaxRecordBytes).Split('\n');
        var after = TextFile.ReadManaged(Path.Combine(directory, JournalFile), ResolutionJournal.MaxJournalBytes);
        if (lines.Length < 5 || lines[0] != "paperq-prune-v1" || lines[^1].Length != 0 ||
            !IsHash(lines[1]) || lines[2] != Hash(after))
        {
            throw InvalidPending();
        }

        var before = ReadJournal(layout);
        if (Hash(before) != lines[1] && Hash(before) != lines[2])
        {
            throw InvalidPending();
        }

        var paths = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines[3..^1])
        {
            var fields = line.Split('\t');
            if (fields.Length != 3 || !PapercutId.IsValid(fields[0]) || !ids.Add(fields[0]) || !IsHash(fields[2]) ||
                fields[1] is not ("open" or "resolved"))
            {
                throw InvalidPending();
            }

            var state = fields[1] == "open" ? QueueState.Open : QueueState.Resolved;
            var path = layout.RecordPath(state, fields[0]);
            foreach (var otherState in new[] { QueueState.Open, QueueState.Working, QueueState.Blocked, QueueState.Resolved })
            {
                if (otherState != state && File.Exists(layout.RecordPath(otherState, fields[0])))
                {
                    throw InvalidPending();
                }
            }

            if (File.Exists(path))
            {
                if (HashBytes(TextFile.ReadRecordSnapshot(path).Bytes) != fields[2])
                {
                    throw InvalidPending();
                }

                paths.Add(path);
            }
            else if (new FileInfo(path).LinkTarget is not null)
            {
                throw InvalidPending();
            }
        }

        PublishArchive(layout, directory);
        if (before != after)
        {
            if (after.Length == 0)
            {
                TextFile.DeleteManaged(layout.ResolutionJournalPath);
            }
            else
            {
                TextFile.Replace(layout.ResolutionJournalPath, after, ResolutionJournal.MaxJournalBytes);
            }
        }

        foreach (var path in paths)
        {
            TextFile.DeleteManaged(path);
        }

        // Delete the plan first: orphan staging files can then be cleaned safely.
        TextFile.DeleteManaged(Path.Combine(directory, PlanFile));
        CleanStaging(directory);
    }

    private static void CleanStaging(string directory)
    {
        foreach (var name in new[] { JournalFile, PreparedPlanFile, ArchiveFile, ArchiveInfoFile })
        {
            TextFile.DeleteManaged(Path.Combine(directory, name));
        }

        Directory.Delete(directory);
    }

    private static void PublishArchive(QueueLayout layout, string directory)
    {
        var infoPath = Path.Combine(directory, ArchiveInfoFile);
        var source = Path.Combine(directory, ArchiveFile);
        if (!File.Exists(infoPath))
        {
            if (File.Exists(source) || new FileInfo(infoPath).LinkTarget is not null)
            {
                throw InvalidPending();
            }

            return;
        }

        var info = TextFile.ReadManaged(infoPath, 1024).Split('\n');
        if (info.Length != 3 || info[2].Length != 0 || !info[0].EndsWith(".zip", StringComparison.Ordinal) ||
            !PapercutId.IsValid(info[0][..^4]) || !IsHash(info[1]))
        {
            throw InvalidPending();
        }

        var archives = Path.Combine(layout.QueueRoot, "archives");
        EnsureDirectory(archives);
        var destination = Path.Combine(archives, info[0]);
        if (File.Exists(source))
        {
            if (File.Exists(destination) || HashArchive(source) != info[1])
            {
                throw InvalidPending();
            }

            File.Move(source, destination, overwrite: false);
        }
        else if (!File.Exists(destination) || new FileInfo(source).LinkTarget is not null || HashArchive(destination) != info[1])
        {
            throw InvalidPending();
        }
    }

    private static string HashArchive(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw InvalidPending();
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static byte[] CaptureRecord(PruneRecord record, StringBuilder plan)
    {
        var snapshot = TextFile.ReadRecordSnapshot(record.FullPath);
        var parsed = RecordCodec.Parse(snapshot.Content, record.FullPath);
        if (parsed.Id != record.Id)
        {
            throw InvalidPending();
        }

        plan.Append($"{record.Id}\t{record.State.ToDirectoryName()}\t{HashBytes(snapshot.Bytes)}\n");
        return snapshot.Bytes;
    }

    private static string WriteArchive(QueueLayout layout, IReadOnlyList<PruneRecord> records,
        string journal, string reason, StringBuilder plan, string stagingDirectory)
    {
        var directory = Path.Combine(layout.QueueRoot, "archives");
        EnsureDirectory(directory);
        var name = PapercutId.Create(DateTimeOffset.UtcNow) + ".zip";
        var temporary = Path.Combine(stagingDirectory, ArchiveFile);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var record in records)
                    {
                        var bytes = CaptureRecord(record, plan);
                        AddZipEntry(zip, layout.RelativePath(record.State, record.Id), bytes, expected);
                    }

                    AddZipEntry(zip, QueueLayout.ResolutionJournalFileName, Encoding.UTF8.GetBytes(journal), expected);
                    var metadata = $"PaperQ prune archive\nCreated: {DateTimeOffset.UtcNow:O}\nReason: {reason}\n";
                    AddZipEntry(zip, "prune.txt", Encoding.UTF8.GetBytes(metadata), expected);
                }

                stream.Flush(flushToDisk: true);
            }

            using (var zip = ZipFile.OpenRead(temporary))
            {
                if (zip.Entries.Count != expected.Count)
                {
                    throw new IOException("The ZIP archive entry count failed verification.");
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in zip.Entries)
                {
                    using var source = entry.Open();
                    if (!seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var hash) ||
                        Convert.ToHexString(SHA256.HashData(source)) != hash)
                    {
                        throw new IOException("The ZIP archive contents failed verification.");
                    }
                }

                if (!seen.SetEquals(expected.Keys))
                {
                    throw new IOException("The ZIP archive filenames failed verification.");
                }
            }

            TextFile.CreateNew(Path.Combine(stagingDirectory, ArchiveInfoFile), $"{name}\n{HashArchive(temporary)}\n");
        }
        catch
        {
            TextFile.DeleteManaged(temporary);
            throw;
        }

        return $".papercuts/archives/{name}";
    }

    private static void AddZipEntry(ZipArchive zip, string name, byte[] bytes, IDictionary<string, string> expected)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
        using var destination = entry.Open();
        destination.Write(bytes);
        expected.Add(name, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static string ReadJournal(QueueLayout layout)
    {
        if (new FileInfo(layout.ResolutionJournalPath).LinkTarget is not null)
        {
            throw new PaperqException("unsafe_file_path", "The resolution journal cannot be a symbolic link.", PaperqExitCode.InvalidData);
        }

        return File.Exists(layout.ResolutionJournalPath)
            ? TextFile.ReadManaged(layout.ResolutionJournalPath, ResolutionJournal.MaxJournalBytes)
            : string.Empty;
    }

    private static void ValidateIds(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            PapercutId.RequireValid(id);
        }
    }

    private static void EnsureDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null || File.Exists(path))
        {
            throw new PaperqException("unsafe_queue_path", $"Expected an ordinary directory: {path}", PaperqExitCode.InvalidData);
        }

        Directory.CreateDirectory(path);
    }

    private static void CheckJournalSize(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) > ResolutionJournal.MaxJournalBytes)
        {
            throw PaperqException.InvalidInput("The resulting resolution journal exceeds its size limit.");
        }
    }

    private static MaintenanceResult Result(IReadOnlyList<string> ids, bool apply, string? mode, string? archive,
        string before, string after, string text) =>
        new(ids, apply, mode, archive, Encoding.UTF8.GetByteCount(before), Encoding.UTF8.GetByteCount(after), text);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string HashBytes(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static bool IsHash(string value) => value.Length == 64 && value.All(character =>
        character is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    private static PaperqException InvalidState(string message) => new("invalid_transition", message, PaperqExitCode.Conflict);

    private static PaperqException InvalidPending() => new("maintenance_conflict",
        "An interrupted pruning operation has conflicting data. Preserve .papercuts/.maintenance and inspect it before retrying.",
        PaperqExitCode.Conflict);
}
