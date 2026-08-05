using System.Text;

namespace Paperq;

internal static class InputRules
{
    internal const int MaxInputBytes = 64 * 1024;
    internal const int MaxRecordBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Validate(string value, string label)
    {
        var normalized = NormalizeLineEndings(value).Trim();
        if (normalized.Length == 0)
        {
            throw PaperqException.InvalidInput($"The {label} cannot be empty.");
        }

        if (normalized.Contains('\0', StringComparison.Ordinal))
        {
            throw PaperqException.InvalidInput($"The {label} cannot contain NUL characters.");
        }

        if (normalized.Contains(RecordCodec.EventsMarker, StringComparison.Ordinal))
        {
            throw PaperqException.InvalidInput($"The {label} contains a reserved paperq marker.");
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(normalized);
        }
        catch (EncoderFallbackException exception)
        {
            throw new PaperqException(
                "invalid_input",
                $"The {label} contains malformed Unicode.",
                PaperqExitCode.InvalidData,
                exception);
        }

        if (byteCount > MaxInputBytes)
        {
            throw PaperqException.InvalidInput(
                $"The {label} is {byteCount} UTF-8 bytes; the limit is {MaxInputBytes} bytes.");
        }

        return normalized;
    }

    internal static string ReadBounded(TextReader reader, string label)
    {
        var buffer = new char[4096];
        var value = new StringBuilder();
        while (true)
        {
            var remaining = MaxInputBytes + 1 - value.Length;
            if (remaining <= 0)
            {
                throw PaperqException.InvalidInput(
                    $"The {label} exceeds the {MaxInputBytes}-byte limit.");
            }

            var read = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }

            value.Append(buffer, 0, read);
        }

        return Validate(value.ToString(), label);
    }

    internal static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace('\r', '\n');
}

