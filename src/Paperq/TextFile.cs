using System.Text;

namespace Paperq;

internal static class TextFile
{
    private const int Utf8BomLength = 3;
    private const int MaxManagedFileBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    internal static string ReadRecord(string path)
    {
        EnsureNotSymbolicLink(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            return ReadUtf8(stream, path, InputRules.MaxRecordBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidUtf8(path, exception);
        }
    }

    internal static FileStream OpenRecordForTransition(string path)
    {
        EnsureNotSymbolicLink(path);
        try
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Delete,
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (DirectoryNotFoundException)
        {
            throw;
        }
    }

    internal static FileStream OpenRecordForClaim(string path)
    {
        EnsureNotSymbolicLink(path);
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);
    }

    internal static string ReadRecord(FileStream stream, string path)
    {
        try
        {
            return ReadUtf8(stream, path, InputRules.MaxRecordBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidUtf8(path, exception);
        }
    }

    internal static void AppendUtf8(FileStream stream, string value, string path)
    {
        var bytes = Utf8WithoutBom.GetBytes(value);
        if (stream.Length + bytes.Length > InputRules.MaxRecordBytes)
        {
            throw new PaperqException(
                "record_too_large",
                $"Updating {path} would exceed the {InputRules.MaxRecordBytes}-byte record limit.",
                PaperqExitCode.InvalidData);
        }

        stream.Position = stream.Length;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static void CreateNew(string path, string content)
    {
        EnsureNotSymbolicLink(path);
        var bytes = Utf8WithoutBom.GetBytes(content);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal static bool Contains(string path, string marker)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        EnsureNotSymbolicLink(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        var content = ReadUtf8(stream, path, MaxManagedFileBytes);
        return content.Contains(marker, StringComparison.Ordinal);
    }

    internal static bool AppendOnce(string path, string marker, string content)
    {
        EnsureNotSymbolicLink(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);

            var existing = ReadUtf8(stream, path, MaxManagedFileBytes);
            if (existing.Contains(marker, StringComparison.Ordinal))
            {
                return false;
            }

            var separator = existing.Length switch
            {
                0 => string.Empty,
                _ when existing.EndsWith("\n\n", StringComparison.Ordinal) => string.Empty,
                _ when existing.EndsWith('\n') => "\n",
                _ => "\n\n",
            };

            var bytes = Utf8WithoutBom.GetBytes(separator + content.TrimEnd() + "\n");
            if (stream.Length + bytes.Length > MaxManagedFileBytes)
            {
                throw new PaperqException(
                    "managed_file_too_large",
                    $"Updating {path} would exceed the {MaxManagedFileBytes}-byte safety limit.",
                    PaperqExitCode.InvalidData);
            }

            stream.Position = stream.Length;
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidUtf8(path, exception);
        }
    }

    internal static bool AppendGitIgnoreRule(string path, string rule)
    {
        EnsureNotSymbolicLink(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);

            var existing = InputRules.NormalizeLineEndings(ReadUtf8(stream, path, MaxManagedFileBytes));
            foreach (var line in existing.Split('\n'))
            {
                var candidate = line.Trim();
                if (candidate.Equals(rule, StringComparison.Ordinal) ||
                    candidate.Equals('/' + rule, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            var separator = existing.Length switch
            {
                0 => string.Empty,
                _ when existing.EndsWith('\n') => string.Empty,
                _ => "\n",
            };
            var bytes = Utf8WithoutBom.GetBytes(separator + rule + "\n");
            stream.Position = stream.Length;
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidUtf8(path, exception);
        }
    }

    internal static bool HasGitIgnoreRule(string path, string rule)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        EnsureNotSymbolicLink(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var existing = InputRules.NormalizeLineEndings(ReadUtf8(stream, path, MaxManagedFileBytes));
            foreach (var line in existing.Split('\n'))
            {
                var candidate = line.Trim();
                if (candidate.Equals(rule, StringComparison.Ordinal) ||
                    candidate.Equals('/' + rule, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidUtf8(path, exception);
        }
    }

    private static string ReadUtf8(FileStream stream, string path, int maximumBytes)
    {
        if (stream.Length > maximumBytes)
        {
            throw new PaperqException(
                "file_too_large",
                $"File exceeds the {maximumBytes}-byte safety limit: {path}",
                PaperqExitCode.InvalidData);
        }

        stream.Position = 0;
        Span<byte> prefix = stackalloc byte[Utf8BomLength];
        var prefixLength = stream.Read(prefix);
        if (prefixLength >= 2 &&
            ((prefix[0] == 0xff && prefix[1] == 0xfe) ||
             (prefix[0] == 0xfe && prefix[1] == 0xff)))
        {
            throw new PaperqException(
                "unsupported_encoding",
                $"Only UTF-8 text files are supported: {path}",
                PaperqExitCode.InvalidData);
        }

        stream.Position = prefixLength >= Utf8BomLength &&
                          prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf
            ? Utf8BomLength
            : 0;

        using var reader = new StreamReader(
            stream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static PaperqException InvalidUtf8(string path, DecoderFallbackException exception) =>
        new(
            "invalid_utf8",
            $"File is not valid UTF-8: {path}",
            PaperqExitCode.InvalidData,
            exception);

    private static void EnsureNotSymbolicLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new PaperqException(
                "unsafe_file_path",
                $"paperq will not read or modify a symbolic link: {path}",
                PaperqExitCode.InvalidData);
        }
    }
}
