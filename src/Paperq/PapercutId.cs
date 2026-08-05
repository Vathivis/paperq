using System.Globalization;
using System.Security.Cryptography;

namespace Paperq;

internal static class PapercutId
{
    private const int TimestampLength = 19;
    private const int EntropyLength = 10;

    internal static string Create(DateTimeOffset createdUtc)
    {
        Span<byte> entropy = stackalloc byte[EntropyLength / 2];
        RandomNumberGenerator.Fill(entropy);
        return $"{createdUtc.UtcDateTime:yyyyMMdd'T'HHmmssfff'Z'}-{Convert.ToHexStringLower(entropy)}";
    }

    internal static bool IsValid(string value)
    {
        if (value.Length != TimestampLength + 1 + EntropyLength || value[TimestampLength] != '-')
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                value.AsSpan(0, TimestampLength),
                "yyyyMMdd'T'HHmmssfff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            return false;
        }

        foreach (var character in value.AsSpan(TimestampLength + 1))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    internal static void RequireValid(string value)
    {
        if (!IsValid(value))
        {
            throw PaperqException.InvalidInput($"Invalid papercut ID: {value}");
        }
    }
}

