namespace Paperq;

internal sealed record ProjectContext(string RootPath, bool IsGitRepository)
{
    internal static ProjectContext Resolve(string? explicitRoot)
    {
        if (explicitRoot is not null)
        {
            var root = NormalizeExistingDirectory(explicitRoot);
            return new ProjectContext(root, HasGitMarker(root));
        }

        var current = NormalizeExistingDirectory(Environment.CurrentDirectory);
        for (var candidate = new DirectoryInfo(current); candidate is not null; candidate = candidate.Parent)
        {
            if (HasGitMarker(candidate.FullName))
            {
                return new ProjectContext(Normalize(candidate.FullName), true);
            }
        }

        return new ProjectContext(current, false);
    }

    private static string NormalizeExistingDirectory(string path)
    {
        string fullPath;
        try
        {
            fullPath = Normalize(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw PaperqException.InvalidInput($"Invalid root path: {exception.Message}");
        }

        if (!Directory.Exists(fullPath))
        {
            throw PaperqException.InvalidInput($"Root directory does not exist: {fullPath}");
        }

        return fullPath;
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool HasGitMarker(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));
}

