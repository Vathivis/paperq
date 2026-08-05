namespace Paperq;

internal sealed record PapercutRecord(
    string Id,
    DateTimeOffset CreatedUtc,
    string Message,
    QueueState State,
    string FullPath);

