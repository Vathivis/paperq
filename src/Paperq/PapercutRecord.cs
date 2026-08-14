namespace Paperq;

internal sealed record PapercutRecord(
    string Id,
    DateTimeOffset CreatedUtc,
    string Message,
    string History,
    QueueState State,
    string FullPath);
