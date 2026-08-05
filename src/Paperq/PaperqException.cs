namespace Paperq;

internal enum PaperqExitCode
{
    Success = 0,
    UnexpectedError = 1,
    UsageError = 2,
    NotInitialized = 3,
    NotFound = 4,
    Conflict = 5,
    InvalidData = 6,
}

internal sealed class PaperqException : Exception
{
    internal PaperqException(string code, string message, PaperqExitCode exitCode)
        : base(message)
    {
        Code = code;
        ExitCode = exitCode;
    }

    internal PaperqException(string code, string message, PaperqExitCode exitCode, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
        ExitCode = exitCode;
    }

    internal string Code { get; }

    internal PaperqExitCode ExitCode { get; }

    internal static PaperqException Usage(string message) =>
        new("usage_error", message, PaperqExitCode.UsageError);

    internal static PaperqException InvalidInput(string message) =>
        new("invalid_input", message, PaperqExitCode.InvalidData);

}

