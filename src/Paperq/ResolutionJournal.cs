using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Paperq;

internal sealed class ResolutionJournal
{
    internal const string HeaderMarker = "<!-- paperq:resolutions -->";
    internal const string EntryMarkerPrefix = "<!-- paperq:resolution:";
    internal const int MaxJournalBytes = 16 * 1024 * 1024;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    private static readonly string Header = """
        <!-- paperq:resolutions -->
        # PaperQ Resolutions

        Project-specific solutions captured when papercuts are resolved. Check this file before repeating a failed approach. Detailed lifecycle history remains in the linked resolved records.
        """;

    private readonly QueueLayout _layout;

    internal ResolutionJournal(QueueLayout layout)
    {
        _layout = layout;
    }

    internal void ValidateExisting()
    {
        using var lockStream = AcquireLock();
        ValidateExistingUnderLock();
    }

    private void ValidateExistingUnderLock()
    {
        if (File.Exists(_layout.ResolutionJournalPath) &&
            !TextFile.Contains(_layout.ResolutionJournalPath, HeaderMarker, MaxJournalBytes))
        {
            throw new PaperqException(
                "invalid_resolution_journal",
                $"Refusing to modify an existing file without the paperq resolution marker: {_layout.ResolutionJournalPath}",
                PaperqExitCode.InvalidData);
        }
    }

    internal bool Append(PapercutRecord record, string note, DateTimeOffset recordedUtc)
    {
        using var lockStream = AcquireLock();
        ValidateExistingUnderLock();
        TextFile.AppendOnce(
            _layout.ResolutionJournalPath,
            HeaderMarker,
            Header,
            MaxJournalBytes);

        var marker = EntryMarker(record.Id, note);
        var entry = FormatEntry(marker, record, note, recordedUtc);
        return TextFile.AppendOnce(
            _layout.ResolutionJournalPath,
            marker,
            entry,
            MaxJournalBytes);
    }

    internal static string EntryMarker(string id, string note)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(note));
        var suffix = Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
        return $"{EntryMarkerPrefix}{id}:{suffix} -->";
    }

    private FileStream AcquireLock()
    {
        var stopwatch = Stopwatch.StartNew();
        IOException? lastException = null;
        while (stopwatch.Elapsed < LockTimeout)
        {
            try
            {
                return TextFile.OpenExclusiveLock(_layout.ResolutionJournalLockPath);
            }
            catch (IOException exception)
            {
                lastException = exception;
                Thread.Sleep(20);
            }
        }

        throw new PaperqException(
            "resolution_journal_busy",
            "The resolution journal is being updated by another paperq process; retry the command.",
            PaperqExitCode.Conflict,
            lastException!);
    }

    private string FormatEntry(
        string marker,
        PapercutRecord record,
        string note,
        DateTimeOffset recordedUtc) =>
        $"""
        {marker}
        ## {record.Id}

        Recorded: {recordedUtc.ToUniversalTime():O}
        Papercut: [{_layout.RelativePath(QueueState.Resolved, record.Id)}]({_layout.RelativePath(QueueState.Resolved, record.Id)})

        ### Problem

        {record.Message}

        ### Resolution

        {note}
        """;
}
