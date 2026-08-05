namespace Paperq;

internal sealed record CliIo(
    TextReader Input,
    TextWriter Output,
    TextWriter Error,
    bool IsInteractive);

