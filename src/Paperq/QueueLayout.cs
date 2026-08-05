namespace Paperq;

internal enum QueueState
{
    Open,
    Working,
    Blocked,
    Resolved,
}

internal static class QueueStateExtensions
{
    internal static string ToDirectoryName(this QueueState state) => state switch
    {
        QueueState.Open => "open",
        QueueState.Working => "working",
        QueueState.Blocked => "blocked",
        QueueState.Resolved => "resolved",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

internal sealed class QueueLayout
{
    internal const string QueueDirectoryName = ".papercuts";

    private static readonly QueueState[] AllStates =
    [
        QueueState.Open,
        QueueState.Working,
        QueueState.Blocked,
        QueueState.Resolved,
    ];

    internal QueueLayout(string rootPath)
    {
        RootPath = rootPath;
        QueueRoot = Path.Combine(rootPath, QueueDirectoryName);
    }

    internal string RootPath { get; }

    internal string QueueRoot { get; }

    internal string StateDirectory(QueueState state) =>
        Path.Combine(QueueRoot, state.ToDirectoryName());

    internal string RecordPath(QueueState state, string id) =>
        Path.Combine(StateDirectory(state), $"{id}.md");

    internal void Create()
    {
        EnsureDirectoryCanBeCreated(QueueRoot);
        Directory.CreateDirectory(QueueRoot);
        EnsureOrdinaryDirectory(QueueRoot);

        foreach (var state in AllStates)
        {
            var path = StateDirectory(state);
            EnsureDirectoryCanBeCreated(path);
            Directory.CreateDirectory(path);
            EnsureOrdinaryDirectory(path);
        }
    }

    internal void RequireInitialized()
    {
        if (!Directory.Exists(QueueRoot))
        {
            throw new PaperqException(
                "not_initialized",
                $"No paperq queue exists at {QueueRoot}. Run 'paperq init' first.",
                PaperqExitCode.NotInitialized);
        }

        EnsureOrdinaryDirectory(QueueRoot);
        foreach (var state in AllStates)
        {
            var path = StateDirectory(state);
            if (!Directory.Exists(path))
            {
                throw new PaperqException(
                    "not_initialized",
                    $"The paperq queue is incomplete; missing directory: {path}. Run 'paperq init' to repair it.",
                    PaperqExitCode.NotInitialized);
            }

            EnsureOrdinaryDirectory(path);
        }
    }

    internal string RelativePath(QueueState state, string id) =>
        $"{QueueDirectoryName}/{state.ToDirectoryName()}/{id}.md";

    private static void EnsureDirectoryCanBeCreated(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
        {
            throw new PaperqException(
                "unsafe_queue_path",
                $"Queue directories cannot be symbolic links or junctions: {path}",
                PaperqExitCode.InvalidData);
        }

        if (File.Exists(path) && !Directory.Exists(path))
        {
            throw new PaperqException(
                "invalid_queue",
                $"Expected a directory but found a file: {path}",
                PaperqExitCode.InvalidData);
        }
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new PaperqException(
                "unsafe_queue_path",
                $"Queue directories cannot be symbolic links or junctions: {path}",
                PaperqExitCode.InvalidData);
        }
    }
}
