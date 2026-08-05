namespace Paperq;

internal sealed class PaperqQueue
{
    private static readonly QueueState[] ActiveStates =
    [
        QueueState.Open,
        QueueState.Working,
        QueueState.Blocked,
    ];

    private static readonly QueueState[] AllStates =
    [
        QueueState.Open,
        QueueState.Working,
        QueueState.Blocked,
        QueueState.Resolved,
    ];

    private readonly TimeProvider _timeProvider;

    internal PaperqQueue(string rootPath, TimeProvider? timeProvider = null)
    {
        Layout = new QueueLayout(rootPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal QueueLayout Layout { get; }

    internal PapercutRecord Add(string rawMessage)
    {
        Layout.RequireInitialized();
        var message = InputRules.Validate(rawMessage, "message");

        for (var attempt = 0; attempt < 16; attempt++)
        {
            var createdUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            var id = PapercutId.Create(createdUtc);
            var path = Layout.RecordPath(QueueState.Open, id);
            var content = RecordCodec.FormatNew(id, createdUtc, message);
            try
            {
                TextFile.CreateNew(path, content);
                return new PapercutRecord(id, createdUtc, message, QueueState.Open, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // An exclusive create plus fresh entropy makes this vanishingly rare.
            }
        }

        throw new PaperqException(
            "id_collision",
            "Could not allocate a unique papercut ID after 16 attempts.",
            PaperqExitCode.Conflict);
    }

    internal IReadOnlyList<PapercutRecord> List(bool includeResolved)
    {
        Layout.RequireInitialized();
        var states = includeResolved ? AllStates : ActiveStates;
        var records = new List<PapercutRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var state in states)
        {
            foreach (var record in ReadState(state))
            {
                if (!ids.Add(record.Id))
                {
                    throw DuplicateRecord(record.Id);
                }

                records.Add(record);
            }
        }

        records.Sort(CompareRecords);
        return records;
    }

    internal PapercutRecord Next(bool claim)
    {
        Layout.RequireInitialized();
        while (true)
        {
            var records = ReadState(QueueState.Open);
            if (records.Count == 0)
            {
                throw new PaperqException(
                    "queue_empty",
                    "There are no open papercuts.",
                    PaperqExitCode.NotFound);
            }

            records.Sort(CompareRecords);
            if (!claim)
            {
                return records[0];
            }

            foreach (var record in records)
            {
                var destination = Layout.RecordPath(QueueState.Working, record.Id);
                if (File.Exists(destination))
                {
                    if (File.Exists(record.FullPath))
                    {
                        throw DuplicateRecord(record.Id);
                    }

                    continue;
                }

                try
                {
                    using var claimHandle = TextFile.OpenRecordForClaim(record.FullPath);
                    if (File.Exists(destination))
                    {
                        if (File.Exists(record.FullPath))
                        {
                            throw DuplicateRecord(record.Id);
                        }

                        // Linux allows concurrent opens before one claimant atomically moves the file.
                        continue;
                    }

                    File.Move(record.FullPath, destination, overwrite: false);
                    return record with { State = QueueState.Working, FullPath = destination };
                }
                catch (FileNotFoundException)
                {
                    // Another process claimed it after enumeration.
                }
                catch (DirectoryNotFoundException) when (!File.Exists(record.FullPath))
                {
                    // Another process claimed it after enumeration.
                }
                catch (IOException) when (!File.Exists(record.FullPath))
                {
                    // Another process claimed it after enumeration.
                }
                catch (IOException exception) when (IsSharingViolation(exception))
                {
                    // Another process may hold the exclusive claim handle. Try the next record.
                }
            }
        }
    }

    internal PapercutRecord Resolve(string id, string rawNote) =>
        Transition(id, QueueState.Working, QueueState.Resolved, "Resolved", InputRules.Validate(rawNote, "note"));

    internal PapercutRecord Block(string id, string rawReason) =>
        Transition(id, QueueState.Working, QueueState.Blocked, "Blocked", InputRules.Validate(rawReason, "reason"));

    internal PapercutRecord Reopen(string id)
    {
        Layout.RequireInitialized();
        PapercutId.RequireValid(id);
        var locations = FindLocations(id);
        if (locations.Count == 0)
        {
            throw NotFound(id);
        }

        if (locations.Count > 1)
        {
            throw DuplicateRecord(id);
        }

        if (locations[0] == QueueState.Open)
        {
            throw new PaperqException(
                "invalid_transition",
                $"Papercut {id} is already open.",
                PaperqExitCode.Conflict);
        }

        return Transition(id, locations[0], QueueState.Open, "Reopened", note: null);
    }

    private PapercutRecord Transition(
        string id,
        QueueState requiredState,
        QueueState destinationState,
        string action,
        string? note)
    {
        Layout.RequireInitialized();
        PapercutId.RequireValid(id);
        var source = Layout.RecordPath(requiredState, id);
        if (!File.Exists(source))
        {
            var locations = FindLocations(id);
            if (locations.Count == 0)
            {
                throw NotFound(id);
            }

            if (locations.Count > 1)
            {
                throw DuplicateRecord(id);
            }

            throw new PaperqException(
                "invalid_transition",
                $"Papercut {id} is {locations[0].ToDirectoryName()}, not {requiredState.ToDirectoryName()}.",
                PaperqExitCode.Conflict);
        }

        var destination = Layout.RecordPath(destinationState, id);
        if (File.Exists(destination))
        {
            throw DuplicateRecord(id);
        }

        try
        {
            using var stream = TextFile.OpenRecordForTransition(source);
            var content = TextFile.ReadRecord(stream, source);
            var parsed = ParseAndVerify(content, source, id);
            var eventText = RecordCodec.FormatEvent(action, note);
            if (!InputRules.NormalizeLineEndings(content).EndsWith(eventText, StringComparison.Ordinal))
            {
                TextFile.AppendUtf8(stream, eventText, source);
            }

            File.Move(source, destination, overwrite: false);
            return new PapercutRecord(id, parsed.CreatedUtc, parsed.Message, destinationState, destination);
        }
        catch (FileNotFoundException)
        {
            throw StateChanged(id);
        }
        catch (DirectoryNotFoundException) when (!File.Exists(source))
        {
            throw StateChanged(id);
        }
        catch (IOException exception) when (IsSharingViolation(exception))
        {
            throw new PaperqException(
                "state_changed",
                $"Papercut {id} is being changed concurrently; retry the command.",
                PaperqExitCode.Conflict,
                exception);
        }
        catch (IOException exception) when (!File.Exists(source))
        {
            throw new PaperqException(
                "state_changed",
                $"Papercut {id} changed state concurrently; retry the command.",
                PaperqExitCode.Conflict,
                exception);
        }
    }

    private List<PapercutRecord> ReadState(QueueState state)
    {
        var records = new List<PapercutRecord>();
        foreach (var path in Directory.EnumerateFiles(Layout.StateDirectory(state), "*.md", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (!PapercutId.IsValid(id))
                {
                    throw new PaperqException(
                        "invalid_record",
                        $"Invalid papercut filename: {path}",
                        PaperqExitCode.InvalidData);
                }

                var content = TextFile.ReadRecord(path);
                var parsed = ParseAndVerify(content, path, id);
                records.Add(new PapercutRecord(id, parsed.CreatedUtc, parsed.Message, state, path));
            }
            catch (FileNotFoundException)
            {
                // Atomic moves may race harmlessly with a listing.
            }
            catch (DirectoryNotFoundException) when (!File.Exists(path))
            {
                // Atomic moves may race harmlessly with a listing.
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                // A claim or lifecycle transition currently owns this record.
            }
        }

        return records;
    }

    private List<QueueState> FindLocations(string id)
    {
        var locations = new List<QueueState>();
        foreach (var state in AllStates)
        {
            if (File.Exists(Layout.RecordPath(state, id)))
            {
                locations.Add(state);
            }
        }

        return locations;
    }

    private static (DateTimeOffset CreatedUtc, string Message) ParseAndVerify(
        string content,
        string path,
        string expectedId)
    {
        var parsed = RecordCodec.Parse(content, path);
        if (!parsed.Id.Equals(expectedId, StringComparison.Ordinal))
        {
            throw new PaperqException(
                "invalid_record",
                $"Papercut ID {parsed.Id} does not match filename {expectedId}: {path}",
                PaperqExitCode.InvalidData);
        }

        return (parsed.CreatedUtc, parsed.Message);
    }

    private static int CompareRecords(PapercutRecord left, PapercutRecord right)
    {
        var byCreated = left.CreatedUtc.CompareTo(right.CreatedUtc);
        return byCreated != 0
            ? byCreated
            : StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xffff) is 32 or 33;

    private static PaperqException NotFound(string id) =>
        new(
            "not_found",
            $"Papercut not found: {id}",
            PaperqExitCode.NotFound);

    private static PaperqException DuplicateRecord(string id) =>
        new(
            "duplicate_record",
            $"Papercut {id} exists in more than one state.",
            PaperqExitCode.Conflict);

    private static PaperqException StateChanged(string id) =>
        new(
            "state_changed",
            $"Papercut {id} changed state concurrently; retry the command.",
            PaperqExitCode.Conflict);
}
