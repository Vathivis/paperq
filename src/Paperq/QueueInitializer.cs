namespace Paperq;

internal sealed class QueueInitializer
{
    internal const string AgentsStartMarker = "<!-- paperq:agent-instructions:start -->";
    internal const string GitIgnoreRule = ".papercuts/";

    internal static readonly string AgentInstructions = """
        <!-- paperq:agent-instructions:start -->
        ## Papercuts

        During normal work, record small, non-blocking friction with `paperq add "<concise message>"` or `paperq add --stdin`, then continue the main task. Examples include dead-end tool calls, broken links, flaky commands, stale caches, confusing errors, and undocumented setup.

        Keep each papercut to one or two sentences. Include a suspected cause or fix only when useful. Never log secrets, credentials, full transcripts, or large raw output.
        <!-- paperq:agent-instructions:end -->
        """;

    private readonly ProjectContext _project;

    internal QueueInitializer(ProjectContext project)
    {
        _project = project;
        Layout = new QueueLayout(project.RootPath);
    }

    internal QueueLayout Layout { get; }

    internal string AgentsPath => Path.Combine(_project.RootPath, "AGENTS.md");

    internal string GitIgnorePath => Path.Combine(_project.RootPath, ".gitignore");

    internal bool IsGitRepository => _project.IsGitRepository;

    internal void CreateQueue() => Layout.Create();

    internal bool HasAgentInstructions() =>
        TextFile.Contains(AgentsPath, AgentsStartMarker);

    internal bool AppendAgentInstructions() =>
        TextFile.AppendOnce(AgentsPath, AgentsStartMarker, AgentInstructions);

    internal bool HasGitIgnoreRule() =>
        _project.IsGitRepository && TextFile.HasGitIgnoreRule(GitIgnorePath, GitIgnoreRule);

    internal bool AddGitIgnoreRule()
    {
        if (!_project.IsGitRepository)
        {
            throw PaperqException.Usage("--gitignore can only be used when the selected root is a Git repository.");
        }

        return TextFile.AppendGitIgnoreRule(GitIgnorePath, GitIgnoreRule);
    }
}

