using System.Globalization;

namespace Paperq;

internal static class RecordCodec
{
    internal const string EventsMarker = "<!-- paperq:events -->";

    private const string Prefix = "# Papercut\n\nID: ";
    private const string CreatedPrefix = "Created: ";
    private const string MessageHeading = "\n\n## Message\n\n";
    private const string HistoryDelimiter = "\n\n" + EventsMarker + "\n## History";

    internal static string FormatNew(string id, DateTimeOffset createdUtc, string message) =>
        $"{Prefix}{id}\n{CreatedPrefix}{createdUtc:O}{MessageHeading}{message}{HistoryDelimiter}\n";

    internal static (string Id, DateTimeOffset CreatedUtc, string Message) Parse(string content, string path)
    {
        var normalized = InputRules.NormalizeLineEndings(content);
        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw InvalidRecord(path, "missing the '# Papercut' header or ID field");
        }

        var idStart = Prefix.Length;
        var idEnd = normalized.IndexOf('\n', idStart);
        if (idEnd < 0)
        {
            throw InvalidRecord(path, "has an incomplete ID field");
        }

        var id = normalized[idStart..idEnd];
        if (!PapercutId.IsValid(id))
        {
            throw InvalidRecord(path, $"contains an invalid ID: {id}");
        }

        var createdStart = idEnd + 1;
        if (!normalized.AsSpan(createdStart).StartsWith(CreatedPrefix, StringComparison.Ordinal))
        {
            throw InvalidRecord(path, "is missing the Created field");
        }

        createdStart += CreatedPrefix.Length;
        var createdEnd = normalized.IndexOf(MessageHeading, createdStart, StringComparison.Ordinal);
        if (createdEnd < 0)
        {
            throw InvalidRecord(path, "is missing the Message section");
        }

        if (!DateTimeOffset.TryParseExact(
                normalized.AsSpan(createdStart, createdEnd - createdStart),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var createdUtc))
        {
            throw InvalidRecord(path, "contains an invalid Created timestamp");
        }

        createdUtc = createdUtc.ToUniversalTime();
        var messageStart = createdEnd + MessageHeading.Length;
        var historyStart = normalized.LastIndexOf(HistoryDelimiter, StringComparison.Ordinal);
        if (historyStart < messageStart)
        {
            throw InvalidRecord(path, "is missing the paperq history marker");
        }

        var message = normalized[messageStart..historyStart];
        try
        {
            message = InputRules.Validate(message, "record message");
        }
        catch (PaperqException exception)
        {
            throw new PaperqException(
                "invalid_record",
                $"Invalid papercut record {path}: {exception.Message}",
                PaperqExitCode.InvalidData,
                exception);
        }

        return (id, createdUtc, message);
    }

    internal static string FormatEvent(string action, string? note)
    {
        if (note is null)
        {
            return $"\n### {action}\n";
        }

        return $"\n### {action}\n\n{note}\n";
    }

    private static PaperqException InvalidRecord(string path, string problem) =>
        new(
            "invalid_record",
            $"Invalid papercut record {path}: {problem}.",
            PaperqExitCode.InvalidData);
}
